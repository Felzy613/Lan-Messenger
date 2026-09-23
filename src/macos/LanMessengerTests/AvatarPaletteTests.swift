import XCTest
@testable import LanMessenger

/// Guards the avatar colour each contact gets. The same name has to get the same
/// colour on a Mac and a PC, launch after launch, and nothing fails loudly when
/// it does not — `String.hashValue` reshuffled every contact on each launch
/// without a single test noticing. `avatar_palette_vector.json` carries the
/// expected values into the Windows suite, which asserts the same file. Its
/// numbers come from the FNV-1a definition, not from either implementation.
/// Mirrors AvatarPaletteTests.cs.
final class AvatarPaletteTests: XCTestCase {

    private struct Vector: Decodable {
        struct Case: Decodable {
            let name: String
            let fnv1a: UInt32
            let index: Int
        }
        let palette: [String]
        let cases: [Case]
    }

    private func vector() throws -> Vector {
        let url: URL
        if let bundled = Bundle(for: AvatarPaletteTests.self)
            .url(forResource: "avatar_palette_vector", withExtension: "json") {
            url = bundled
        } else {
            url = URL(fileURLWithPath: #file).deletingLastPathComponent()
                .appendingPathComponent("avatar_palette_vector.json")
        }
        return try JSONDecoder().decode(Vector.self, from: try Data(contentsOf: url))
    }

    func testThePaletteIsTheSharedEightInOrder() throws {
        let expected = try vector().palette
        XCTAssertEqual(AvatarPalette.rgb.map { String(format: "#%06x", $0) }, expected)
    }

    func testEveryNameHashesToTheSharedValue() throws {
        for testCase in try vector().cases {
            XCTAssertEqual(AvatarPalette.fnv1a(testCase.name), testCase.fnv1a,
                           testCase.name.debugDescription)
        }
    }

    func testEveryNameGetsTheSharedIndex() throws {
        let cases = try vector().cases
        for testCase in cases {
            XCTAssertEqual(AvatarPalette.index(for: testCase.name), testCase.index,
                           testCase.name.debugDescription)
        }
        // A vector trimmed to a couple of names would still pass while pinning
        // almost nothing.
        XCTAssertEqual(Set(cases.map(\.index)), Set(0..<8), "the vector should reach every slot")
    }
}
