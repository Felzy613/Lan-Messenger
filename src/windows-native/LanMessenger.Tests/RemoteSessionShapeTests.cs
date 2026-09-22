using LanMessenger.Core.Networking.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Linq;
using System.Text.Json;

namespace LanMessenger.Tests;

// The Windows mirrors of the grant ladder, the stop reasons, the audit trail and
// the NV12 conversion. Same assertions as the Swift suites, because a mirror
// that has drifted is worse than no mirror.
[TestClass]
public class RemoteSessionShapeTests
{
    // ---- The grant ladder --------------------------------------------------

    [TestMethod]
    public void ControlIsUnreachableWithoutViewing()
    {
        // The invariant the whole two-stage design exists for: no code path
        // grants input to a session nobody agreed to watch.
        var state = new RemoteGrantState();
        Assert.IsFalse(state.GrantControl(), "control was granted from a cold start");
        Assert.AreEqual(RemoteGrant.None, state.Grant);
        Assert.IsFalse(state.AcceptsInput);
    }

    [TestMethod]
    public void AcceptGrantsViewingAndNothingElse()
    {
        var state = new RemoteGrantState();
        Assert.IsTrue(state.Accept());
        Assert.AreEqual(RemoteGrant.Viewing, state.Grant);
        Assert.IsFalse(state.AcceptsInput, "accepting an invite must not arm the input channel");
    }

    [TestMethod]
    public void RevokeDropsInputButKeepsTheSession()
    {
        var state = new RemoteGrantState();
        state.Accept();
        state.GrantControl();
        Assert.IsTrue(state.RevokeControl());
        Assert.AreEqual(RemoteGrant.Viewing, state.Grant);
        Assert.IsTrue(state.IsLive, "revoking control must not end the session");
    }

    [TestMethod]
    public void EndIsTerminal()
    {
        var state = new RemoteGrantState();
        state.Accept();
        state.End();
        Assert.IsFalse(state.Accept(), "an ended session must not resume on an accept");
        Assert.IsFalse(state.GrantControl());
        Assert.IsTrue(state.Ended);
    }

    [TestMethod]
    public void OnlyControlAcceptsInput()
    {
        Assert.IsFalse(RemoteGrant.None.AcceptsInput());
        Assert.IsFalse(RemoteGrant.Viewing.AcceptsInput());
        Assert.IsTrue(RemoteGrant.Control.AcceptsInput());
    }

    // ---- Stop reasons ------------------------------------------------------

    [TestMethod]
    public void StopTokensMatchTheSwiftSide()
    {
        // These land in stored history and travel in remote_end. A mirror that
        // disagrees writes a record the other platform cannot read back.
        Assert.AreEqual("user_stopped", RemoteStopReason.UserStopped.ToToken());
        Assert.AreEqual("kill_switch", RemoteStopReason.KillSwitch.ToToken());
        Assert.AreEqual("network_lost", RemoteStopReason.NetworkLost.ToToken());
        Assert.AreEqual("screen_locked", RemoteStopReason.ScreenLocked.ToToken());

        foreach (RemoteStopReason r in Enum.GetValues<RemoteStopReason>())
        {
            Assert.AreEqual(r.ToToken(), r.ToToken().ToLowerInvariant(), $"{r} has uppercase");
            // Both roles: a viewer writes these lines too, and a missing case
            // there would be an empty sentence in somebody's history.
            foreach (bool viewing in new[] { false, true })
            {
                string line = r.AuditDescription(viewing);
                Assert.IsFalse(string.IsNullOrEmpty(line), $"{r} has no audit line");
                Assert.IsTrue(line.EndsWith('.'), $"{r}: audit lines are sentences");
                Assert.IsFalse(line.Contains('_'), $"{r}: a wire token leaked into the audit line");
            }
        }
    }

    [TestMethod]
    public void ACleanChannelCloseIsThePeerEndingNotAFault()
    {
        // The bug: both call sites passed RemoteStopReason.Error
        // unconditionally, so a session the OTHER side ended on purpose was
        // recorded as "stopped because of an error" — while the log one line
        // above already said `peer ended`. The handler had the answer the whole
        // time: a clean close carries no fault.
        Assert.AreEqual(RemoteStopReason.PeerEnded,
            RemoteStopReasonExtensions.ForChannelClose(null, RemoteStopReason.Error));

        Assert.AreEqual(RemoteStopReason.Error,
            RemoteStopReasonExtensions.ForChannelClose(
                MediaFaultKind.LinkFailed, RemoteStopReason.Error));

        // The fallback is the caller's, because what an errored close means
        // differs by role and by platform.
        Assert.AreEqual(RemoteStopReason.NetworkLost,
            RemoteStopReasonExtensions.ForChannelClose(
                MediaFaultKind.LinkFailed, RemoteStopReason.NetworkLost));
    }

    [TestMethod]
    public void TheTwoClosesReadDifferentlyInTheTrail()
    {
        // The point of the distinction, at the only place a user sees it.
        Assert.AreEqual("The other side ended the session.",
            RemoteStopReason.PeerEnded.AuditDescription(viewing: true));
        Assert.AreNotEqual(RemoteStopReason.PeerEnded.AuditDescription(viewing: true),
                           RemoteStopReason.NetworkLost.AuditDescription(viewing: true));
    }

    [TestMethod]
    public void AViewerNeverClaimsItsOwnScreenWasShared()
    {
        // The bug: every one of these sentences was written from the host's
        // chair, so a PC that had spent ten minutes WATCHING somebody else's
        // screen ended the session and recorded "You stopped sharing your
        // screen." That is not a wording slip — it is a false entry in the one
        // record a user consults to find out whether their screen was shared.
        foreach (RemoteStopReason r in Enum.GetValues<RemoteStopReason>())
        {
            string line = r.AuditDescription(viewing: true).ToLowerInvariant();
            Assert.IsFalse(line.Contains("sharing your screen"),
                           $"{r}: a viewer's line claims its own screen was shared");
            Assert.IsFalse(line.Contains("screen sharing stopped"),
                           $"{r}: a viewer's line is written from the host's chair");
        }

        Assert.AreEqual("You stopped sharing your screen.",
                        RemoteStopReason.UserStopped.AuditDescription(viewing: false));
        Assert.AreEqual("You stopped viewing their screen.",
                        RemoteStopReason.UserStopped.AuditDescription(viewing: true));

        // The one cause that reads identically from both chairs: the peer's
        // decision is the peer's decision whichever end we are.
        Assert.AreEqual(RemoteStopReason.PeerEnded.AuditDescription(viewing: false),
                        RemoteStopReason.PeerEnded.AuditDescription(viewing: true));
    }

    [TestMethod]
    public void ReasonsWithNoLinkCannotNotifyThePeer()
    {
        Assert.IsFalse(RemoteStopReason.NetworkLost.CanNotifyPeer());
        Assert.IsFalse(RemoteStopReason.PeerEnded.CanNotifyPeer());
        Assert.IsTrue(RemoteStopReason.UserStopped.CanNotifyPeer());
        Assert.IsTrue(RemoteStopReason.Watchdog.CanNotifyPeer());
    }

    [TestMethod]
    public void TheKillShortcutIsReservedAndOrdinaryKeysAreNot()
    {
        var s = RemoteKillSwitch.Shortcut;
        Assert.IsTrue(RemoteKillSwitch.IsReserved(s.Modifiers, s.VirtualKey));
        Assert.IsFalse(RemoteKillSwitch.IsReserved(0, 0x1B), "bare Escape is not ours");
        Assert.IsFalse(RemoteKillSwitch.IsReserved(0x0002, 0x41), "Ctrl+A is not ours");
        Assert.AreEqual("Ctrl+Alt+Shift+Esc", s.DisplayName);
    }

    // ---- The audit trail ---------------------------------------------------

    [TestMethod]
    public void AnAuditRecordSurvivesARoundTrip()
    {
        foreach (RemoteAuditEvent e in Enum.GetValues<RemoteAuditEvent>())
        {
            var record = new RemoteAuditRecord(e, "Dave", "user_stopped", 90);
            var decoded = RemoteAuditRecord.Decode(record.Encoded());
            Assert.IsNotNull(decoded, $"{e} did not survive storage");
            Assert.AreEqual(e, decoded!.Event);
            Assert.AreEqual("Dave", decoded.PeerName);
        }
    }

    [TestMethod]
    public void OrdinaryMessagesAreNotMistakenForAuditRecords()
    {
        Assert.IsNull(RemoteAuditRecord.Decode("hello"));
        Assert.IsNull(RemoteAuditRecord.Decode("__FILE__:/tmp/a.png"));
        Assert.IsNull(RemoteAuditRecord.Decode(""));
        Assert.IsFalse(RemoteAuditRecord.IsAudit("I sent __REMOTE__: by accident"));
    }

    [TestMethod]
    public void AMalformedOrFutureRecordIsIgnoredRatherThanFatal()
    {
        Assert.IsNull(RemoteAuditRecord.Decode("__REMOTE__:not json"));
        Assert.IsNull(RemoteAuditRecord.Decode("__REMOTE__:{}"));
        Assert.IsNull(RemoteAuditRecord.Decode("""__REMOTE__:{"event":"teleported","peerName":"Dave"}"""),
                      "an unknown event must decode to nothing, not throw");
    }

    [TestMethod]
    public void TheEndReasonBecomesProse()
    {
        var record = new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "Dave", "screen_locked");
        Assert.AreEqual(RemoteStopReason.ScreenLocked.AuditDescription(), record.Summary);
        Assert.IsFalse(record.Summary.Contains('_'), "a wire token leaked into the trail");
    }

    [TestMethod]
    public void AViewersTrailNeverSaysItsOwnScreenWasShared()
    {
        // Mirror of AViewersTrailNeverSaysItsOwnScreenWasShared in the Swift
        // suite. The two write into the same history format and are read by the
        // same person, so the sentences have to agree.
        Assert.AreEqual("You started viewing Dave's screen.",
            new RemoteAuditRecord(RemoteAuditEvent.SessionStarted, "Dave", viewing: true).Summary);
        Assert.AreEqual("Dave started viewing your screen.",
            new RemoteAuditRecord(RemoteAuditEvent.SessionStarted, "Dave").Summary);

        Assert.AreEqual("Dave gave you control of their screen.",
            new RemoteAuditRecord(RemoteAuditEvent.ControlGranted, "Dave", viewing: true).Summary);
        Assert.AreEqual("You gave Dave control of your screen.",
            new RemoteAuditRecord(RemoteAuditEvent.ControlGranted, "Dave").Summary);

        Assert.AreEqual("You stopped viewing their screen.",
            new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "Dave", "user_stopped",
                                  viewing: true).Summary);
    }

    [TestMethod]
    public void ARecordWrittenBeforeTheRoleExistedReadsAsAHost()
    {
        // `viewing` is optional precisely so stored history needs no migration.
        // Absent has to mean host, because that is what every record written
        // before this field existed was.
        var legacy = RemoteAuditRecord.Decode(
            """__REMOTE__:{"event":"session_started","peerName":"Dave"}""");
        Assert.IsNotNull(legacy, "a record without `viewing` no longer decodes");
        Assert.IsFalse(legacy!.WasViewing);
        Assert.AreEqual("Dave started viewing your screen.", legacy.Summary);
    }

    [TestMethod]
    public void AHostsRecordKeepsTheBytesItAlwaysHad()
    {
        // Written only when true, so the common case is byte-identical to what
        // older builds stored and a diff of history stays readable.
        Assert.IsFalse(new RemoteAuditRecord(RemoteAuditEvent.SessionStarted, "Dave")
                            .Encoded().Contains("viewing"));
        Assert.IsTrue(new RemoteAuditRecord(RemoteAuditEvent.SessionStarted, "Dave", viewing: true)
                            .Encoded().Contains("\"viewing\":true"));
    }

    [TestMethod]
    public void OnlyTheStoredFieldsAreWrittenAndNotTheDerivedOnes()
    {
        // System.Text.Json writes get-only properties, so this record used to
        // store Event, Summary and DurationSummary beside the four real fields:
        // keys the Swift record never writes, and RENDERED SENTENCES frozen into
        // history that a later change of wording would leave stale. The two
        // platforms write into the same history and are supposed to produce the
        // same bytes.
        //
        // Found by a test that assumed a host's record contained no "viewing"
        // and was defeated by "started viewing your screen." inside a serialized
        // Summary.
        string json = new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "Dave",
                                            "user_stopped", 12).Encoded();
        json = json[RemoteAuditRecord.Marker.Length..];

        using var document = JsonDocument.Parse(json);
        var keys = document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n);
        CollectionAssert.AreEqual(new[] { "duration", "event", "peerName", "reason" },
                                  keys.ToArray(),
                                  $"the stored record's shape drifted: {json}");
    }

    [TestMethod]
    public void DurationIsReportedInUnitsAPersonUses()
    {
        Assert.AreEqual("Lasted 7s.", new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "D", null, 7).DurationSummary);
        Assert.AreEqual("Lasted 1m 30s.", new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "D", null, 90).DurationSummary);
        Assert.AreEqual("Lasted 1h 1m.", new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "D", null, 3700).DurationSummary);
        Assert.IsNull(new RemoteAuditRecord(RemoteAuditEvent.SessionEnded, "D", null, 0).DurationSummary,
                      "\"Lasted 0s\" is noise on a session that never ran");
    }

    [TestMethod]
    public void TheMarkerCannotCollideWithTheAttachmentPrefix()
    {
        Assert.AreNotEqual("__FILE__:", RemoteAuditRecord.Marker);
        Assert.IsFalse(RemoteAuditRecord.Marker.StartsWith("__FILE__:"));
        Assert.IsFalse("__FILE__:".StartsWith(RemoteAuditRecord.Marker));
    }

    // ---- NV12 to BGRA ------------------------------------------------------

    [TestMethod]
    public void StrideIsHonouredRatherThanAssumedEqualToWidth()
    {
        // The classic failure, and it does not look like a stride bug: each row
        // lands progressively further left and the picture shears into a
        // diagonal smear, which reads as a corrupt stream.
        const int width = 4, height = 2, stride = 8;
        var nv12 = new byte[stride * height * 3 / 2];

        // Row 0 luma bright, row 1 luma dark; the padding between them is junk
        // that must never be read as picture.
        for (int x = 0; x < width; x++) { nv12[x] = 235; nv12[stride + x] = 16; }
        for (int x = width; x < stride; x++) { nv12[x] = 99; nv12[stride + x] = 99; }
        for (int i = stride * height; i < nv12.Length; i++) nv12[i] = 128;

        var bgra = new byte[Nv12Converter.BgraLength(width, height)];
        Nv12Converter.ToBgra(nv12, stride, width, height, bgra);

        // Top row white-ish, bottom row black-ish. If stride were treated as
        // width the second row would pick up the 99 padding instead.
        Assert.IsTrue(bgra[0] > 200, "top-left should be bright");
        int bottomLeft = height / 2 * width * 4 * 0 + width * 4; // row 1, x 0
        Assert.IsTrue(bgra[bottomLeft] < 40, "bottom-left should be dark, not padding");
    }

    [TestMethod]
    public void ChromaIsReadFromTheSurfaceHeightNotTheDisplayHeight()
    {
        // The regression this exists for: cropping 1088 down to 1080 for display
        // also moved the chroma read, because the offset was computed from the
        // displayed height. The picture stayed perfectly sharp — luma is
        // untouched — and every colour in it was wrong, which reads as a broken
        // decoder rather than as an arithmetic slip in the presenter.
        const int width = 4, height = 4, surfaceHeight = 8, stride = 4;

        var nv12 = new byte[stride * surfaceHeight * 3 / 2];

        // Mid-grey luma across the whole surface, including the 4 padding rows.
        for (int i = 0; i < stride * surfaceHeight; i++) nv12[i] = 128;

        // Real chroma begins after ALL surfaceHeight rows. Strongly blue:
        // Cb high, Cr low.
        for (int i = stride * surfaceHeight; i < nv12.Length; i += 2)
        {
            nv12[i] = 240;      // Cb
            nv12[i + 1] = 16;   // Cr
        }

        var bgra = new byte[Nv12Converter.BgraLength(width, height)];
        Nv12Converter.ToBgra(nv12, stride, width, height, bgra, surfaceHeight);

        // Blue channel first in BGRA.
        Assert.IsTrue(bgra[0] > bgra[2],
            "with Cb high and Cr low the result must be blue-dominant");

        // And the failure mode, stated explicitly: read chroma at the display
        // height and the 128s of padding luma are used as chroma instead, which
        // is exactly neutral — no colour at all.
        var wrong = new byte[Nv12Converter.BgraLength(width, height)];
        Nv12Converter.ToBgra(nv12, stride, width, height, wrong, height);
        Assert.AreEqual(wrong[0], wrong[2],
            "the offset-by-padding case should come out neutral, proving the two differ");
    }

    [TestMethod]
    public void AShortBufferIsTruncatedRatherThanOverrun()
    {
        // Frames arrive from a peer. A decoder that under-delivers must not walk
        // off the end of the array.
        var nv12 = new byte[10];
        var bgra = new byte[Nv12Converter.BgraLength(64, 64)];
        Nv12Converter.ToBgra(nv12, 64, 64, 64, bgra);   // must not throw
    }

    [TestMethod]
    public void TheBgraBufferSizeIsFourBytesAPixel()
    {
        Assert.AreEqual(1920 * 1080 * 4, Nv12Converter.BgraLength(1920, 1080));
    }
}
