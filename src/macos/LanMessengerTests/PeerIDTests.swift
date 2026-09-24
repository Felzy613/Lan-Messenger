import XCTest
@testable import LanMessenger

// Conversations are filed by identity key, not by LAN address. See PeerID.swift
// and PROTOCOL.md → History.
//
// The migration is the part worth guarding: it runs once over history written
// under addresses, and the failure it exists to prevent — one person's messages
// shown as another's — is exactly what a careless guess at an ambiguous address
// would produce. So the tests are mostly about what it refuses to do.
final class PeerIDTests: XCTestCase {

    // Real-shaped keys: base64 of 32 bytes.
    private let dell = Data(repeating: 0x11, count: 32).base64EncodedString()
    private let ari  = Data(repeating: 0x22, count: 32).base64EncodedString()

    private func entry(_ id: String?, at t: Double, _ text: String = "x") -> MessageEntry {
        MessageEntry(sender: "p", text: text, incoming: true, timestamp: t,
                     messageId: id, status: "", readReceiptSent: false)
    }

    // MARK: - What an id is

    func testAKeyIsAnIdAndAnAddressIsNot() {
        XCTAssertTrue(PeerID.isKey(dell))
        XCTAssertFalse(PeerID.isKey("192.168.1.27"))
        XCTAssertFalse(PeerID.isKey("ip:192.168.1.27"))
        // Base64, but not 32 bytes: not a key.
        XCTAssertFalse(PeerID.isKey("AQIDBA=="))
        XCTAssertFalse(PeerID.isKey(""))
    }

    func testLegacyIdsRoundTripTheirAddress() {
        let id = PeerID.legacy(address: "192.168.1.27")
        XCTAssertEqual(id, "ip:192.168.1.27")
        XCTAssertTrue(PeerID.isLegacy(id))
        XCTAssertEqual(PeerID.legacyAddress(id), "192.168.1.27")
        XCTAssertNil(PeerID.legacyAddress(dell))
    }

    // MARK: - Resolving one name

    func testAnAddressOwnedByExactlyOneContactBecomesTheirKey() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27"),
                        PeerID.Contact(publicKeyB64: ari, lastIP: "192.168.1.31")]
        XCTAssertEqual(PeerID.resolve(legacyName: "192.168.1.27", contacts: contacts), dell)
        XCTAssertEqual(PeerID.resolve(legacyName: "192.168.1.31", contacts: contacts), ari)
    }

    /// Two contacts recorded at one address is the DHCP collision itself.
    /// Picking either would hand one person's thread to the other.
    func testAnAddressTwoContactsShareIsNotGuessed() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27"),
                        PeerID.Contact(publicKeyB64: ari, lastIP: "192.168.1.27")]
        XCTAssertEqual(PeerID.resolve(legacyName: "192.168.1.27", contacts: contacts),
                       "ip:192.168.1.27")
    }

    func testAnAddressNoContactOwnsIsKeptUnderItsAddress() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27")]
        XCTAssertEqual(PeerID.resolve(legacyName: "192.168.1.9", contacts: contacts),
                       "ip:192.168.1.9")
    }

    func testKeysAndLegacyIdsPassThrough() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27")]
        XCTAssertEqual(PeerID.resolve(legacyName: ari, contacts: contacts), ari)
        XCTAssertEqual(PeerID.resolve(legacyName: "ip:192.168.1.27", contacts: contacts),
                       "ip:192.168.1.27")
    }

    func testARelayPlaceholderResolvesToTheOneContactWithThatKeyPrefix() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27"),
                        PeerID.Contact(publicKeyB64: ari, lastIP: "192.168.1.31")]
        let name = "relay-\(dell.prefix(8))"
        XCTAssertEqual(PeerID.resolve(legacyName: name, contacts: contacts), dell)
        // Nobody with that prefix: kept, and under exactly the name the live
        // re-filing in AppModel looks for once that peer turns up.
        XCTAssertEqual(PeerID.resolve(legacyName: name, contacts: []),
                       PeerID.relayPlaceholder(forKey: dell))
    }

    // MARK: - Re-filing a whole history

    /// A contact that moved address had a bucket under each. Both land on the
    /// key and merge: de-duplicated, in time order, capped.
    func testBucketsThatLandOnOneKeyMerge() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27")]
        let history: [String: [MessageEntry]] = [
            "192.168.1.27": [entry("b", at: 2), entry("d", at: 4)],
            dell: [entry("a", at: 1), entry("b", at: 2), entry("c", at: 3)],
        ]
        let (result, moved) = PeerID.rekey(history: history, contacts: contacts, cap: 200)

        XCTAssertEqual(moved, ["192.168.1.27": dell])
        XCTAssertEqual(Array(result.keys), [dell])
        XCTAssertEqual(result[dell]?.map(\.messageId), ["a", "b", "c", "d"])
    }

    func testAMergeKeepsTheNewestWithinTheCap() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27")]
        let history: [String: [MessageEntry]] = [
            "192.168.1.27": [entry("a", at: 1), entry("c", at: 3)],
            dell: [entry("b", at: 2), entry("d", at: 4)],
        ]
        let (result, _) = PeerID.rekey(history: history, contacts: contacts, cap: 3)
        XCTAssertEqual(result[dell]?.map(\.messageId), ["b", "c", "d"])
    }

    /// Messages sharing a timestamp keep the order they arrived in, and
    /// entries with no id (older attachments) are never de-duplicated away.
    func testTheMergeIsStableAndKeepsIdlessEntries() {
        let merged = PeerID.merge([[entry(nil, at: 5, "first"), entry("x", at: 5, "second")],
                                   [entry(nil, at: 5, "third"), entry("x", at: 5, "dup")]],
                                  cap: 200)
        XCTAssertEqual(merged.map(\.text), ["first", "second", "third"])
    }

    /// A bucket with one source is left exactly as it was — re-sorting it
    /// could swap two messages that share a timestamp.
    func testASingleBucketIsNotReordered() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27")]
        let original = [entry("late", at: 9), entry("early", at: 1)]
        let (result, _) = PeerID.rekey(history: ["192.168.1.27": original],
                                       contacts: contacts, cap: 200)
        XCTAssertEqual(result[dell]?.map(\.messageId), ["late", "early"])
    }

    /// Runs at every load, so a migrated history must pass through untouched
    /// and report nothing moved — otherwise every launch rewrites the file.
    func testRekeyingIsIdempotent() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27")]
        let history: [String: [MessageEntry]] = [
            "192.168.1.27": [entry("a", at: 1)],
            "192.168.1.9": [entry("b", at: 2)],
        ]
        let (once, _) = PeerID.rekey(history: history, contacts: contacts, cap: 200)
        let (twice, moved) = PeerID.rekey(history: once, contacts: contacts, cap: 200)
        XCTAssertTrue(moved.isEmpty)
        XCTAssertEqual(twice.keys.sorted(), once.keys.sorted())
        XCTAssertEqual(twice[dell]?.map(\.messageId), ["a"])
        XCTAssertEqual(twice["ip:192.168.1.9"]?.map(\.messageId), ["b"])
    }

    func testListsAreRekeyedInOrderWithoutDuplicates() {
        let contacts = [PeerID.Contact(publicKeyB64: dell, lastIP: "192.168.1.27")]
        XCTAssertEqual(
            PeerID.rekey(list: ["192.168.1.27", "192.168.1.9", dell], contacts: contacts),
            [dell, "ip:192.168.1.9"])
    }

    // MARK: - HistoryStore.merge

    func testHistoryStoreMergesAPlaceholderIntoTheKey() {
        let placeholder = PeerID.relayPlaceholder(forKey: dell)
        defer {
            HistoryStore.shared.delete(peer: placeholder)
            HistoryStore.shared.delete(peer: dell)
        }
        HistoryStore.shared.append(entry: entry("relayed", at: 1), forPeer: placeholder)
        HistoryStore.shared.append(entry: entry("lan", at: 2), forPeer: dell)

        XCTAssertTrue(HistoryStore.shared.merge(from: placeholder, into: dell))
        XCTAssertEqual(HistoryStore.shared.entries(forPeer: dell).map(\.messageId),
                       ["relayed", "lan"])
        XCTAssertTrue(HistoryStore.shared.entries(forPeer: placeholder).isEmpty)
        XCTAssertFalse(HistoryStore.shared.merge(from: placeholder, into: dell),
                       "nothing left to move")
    }
}

// Unencrypted packets — typing, receipts, delete_message — only CLAIM a sender
// key. Filed by that claim alone they would need no connection from anywhere in
// particular, so a claim is accepted only from an address the key is known at.
@MainActor
final class ClaimedSenderBindingTests: XCTestCase {

    private let key = Data(repeating: 0x33, count: 32).base64EncodedString()
    private var savedBinding: ((String, String) -> Bool)?
    private var savedTyping: ((String, String, Bool) -> Void)?
    private var typed: [(peer: String, active: Bool)] = []

    override func setUp() async throws {
        savedBinding = MessagingService.shared.isBoundAddress
        savedTyping = MessagingService.shared.onTypingUpdate
        typed = []
        MessagingService.shared.onTypingUpdate = { [weak self] peer, _, active in
            self?.typed.append((peer, active))
        }
        let key = self.key
        MessagingService.shared.isBoundAddress = { k, ip in k == key && ip == "192.168.1.27" }
    }

    override func tearDown() async throws {
        MessagingService.shared.isBoundAddress = savedBinding
        MessagingService.shared.onTypingUpdate = savedTyping
        HistoryStore.shared.delete(peer: key)
    }

    private func typing(from sender: String, at ip: String) {
        let pkt = TypingPacket(type: "typing", active: true, sender: "Dell",
                               senderPublicKeyB64: sender, port: 54232)
        MessagingService.shared.handlePacket(.typing(pkt, senderIP: ip))
    }

    func testAClaimFromTheKeysAddressIsFiledUnderTheKey() {
        typing(from: key, at: "192.168.1.27")
        XCTAssertEqual(typed.map(\.peer), [key])
    }

    /// Somebody else on the LAN naming the Dell's key: dropped, not shown as
    /// the Dell typing.
    func testAClaimFromAnyOtherAddressIsDropped() {
        typing(from: key, at: "192.168.1.31")
        XCTAssertTrue(typed.isEmpty)
    }

    func testASenderKeyThatIsNotAKeyIsDropped() {
        MessagingService.shared.isBoundAddress = nil   // would accept anything
        typing(from: "192.168.1.27", at: "192.168.1.27")
        XCTAssertTrue(typed.isEmpty)
    }

    func testAnUnboundDeleteLeavesTheMessageAlone() {
        HistoryStore.shared.append(
            entry: MessageEntry(sender: "Dell", text: "keep me", incoming: true, timestamp: 1,
                                messageId: "del-unbound", status: "", readReceiptSent: false),
            forPeer: key)
        let pkt = ReceiptPacket(type: "delete_message", messageId: "del-unbound",
                                sender: "Dell", senderPublicKeyB64: key, port: 54232)
        MessagingService.shared.handlePacket(.delete(pkt, senderIP: "192.168.1.31"))

        let stored = HistoryStore.shared.entries(forPeer: key).first
        XCTAssertEqual(stored?.text, "keep me")
        XCTAssertEqual(stored?.deleted, false)
    }
}
