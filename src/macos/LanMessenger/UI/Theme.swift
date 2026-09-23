import SwiftUI

enum Theme {
    static let accent = Color(red: 37 / 255, green: 211 / 255, blue: 102 / 255)

    static func sidebarBackground(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 17 / 255, green: 27 / 255, blue: 33 / 255)
            : Color(red: 240 / 255, green: 242 / 255, blue: 245 / 255)
    }

    static func chatBackground(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 13 / 255, green: 20 / 255, blue: 24 / 255)
            : Color(red: 229 / 255, green: 221 / 255, blue: 213 / 255)
    }

    static func outgoingBubble(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 0, green: 92 / 255, blue: 75 / 255)
            : Color(red: 220 / 255, green: 248 / 255, blue: 198 / 255)
    }

    static func incomingBubble(_ scheme: ColorScheme) -> Color {
        scheme == .dark
            ? Color(red: 32 / 255, green: 44 / 255, blue: 51 / 255)
            : .white
    }

    private static let avatarPalette: [Color] = AvatarPalette.rgb.map { rgb in
        Color(red: Double((rgb >> 16) & 0xFF) / 255,
              green: Double((rgb >> 8) & 0xFF) / 255,
              blue: Double(rgb & 0xFF) / 255)
    }

    static func avatarColor(for name: String) -> Color {
        avatarPalette[AvatarPalette.index(for: name)]
    }

    static func initials(for name: String) -> String {
        let words = name.split(separator: " ")
        if words.count >= 2 {
            return (String(words[0].prefix(1)) + String(words[1].prefix(1))).uppercased()
        }
        return String(name.prefix(2)).uppercased()
    }

    static func formatTimestamp(_ date: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(date) {
            return date.formatted(.dateTime.hour().minute())
        } else if cal.isDateInYesterday(date) {
            return "Yesterday"
        } else if let days = cal.dateComponents([.day], from: date, to: Date()).day, days < 7 {
            return date.formatted(.dateTime.weekday(.abbreviated))
        } else {
            return date.formatted(.dateTime.day().month(.twoDigits).year(.twoDigits))
        }
    }
}
