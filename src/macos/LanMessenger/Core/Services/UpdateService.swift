import Foundation

struct UpdateInfo {
    let version: String
    let notes: String
    let macosDownloadURL: String
}

// Checks the remote update manifest and compares against APP_VERSION.
// Calls back on the main queue.
final class UpdateService {

    static let shared = UpdateService()
    static let appVersion: String = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0.0"

    private init() {}

    enum UpdateResult {
        case upToDate
        case available(UpdateInfo)
        case error(String)
    }

    func check(manifestURL: String, completion: @escaping (UpdateResult) -> Void) {
        guard let url = URL(string: manifestURL), !manifestURL.isEmpty else {
            DispatchQueue.main.async { completion(.error("No update server configured")) }
            return
        }
        var request = URLRequest(url: url, timeoutInterval: 10)
        request.cachePolicy = .reloadIgnoringLocalCacheData

        URLSession.shared.dataTask(with: request) { data, _, error in
            if let error {
                DispatchQueue.main.async { completion(.error(error.localizedDescription)) }
                return
            }
            guard let data,
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let remoteVersion = json["version"] as? String else {
                DispatchQueue.main.async { completion(.error("Invalid manifest")) }
                return
            }
            if Self.version(remoteVersion, isNewerThan: Self.appVersion) {
                let notes = json["notes"] as? String ?? ""
                let downloads = json["downloads"] as? [String: String] ?? [:]
                let macURL = downloads["macos"] ?? ""
                let info = UpdateInfo(version: remoteVersion, notes: notes, macosDownloadURL: macURL)
                DispatchQueue.main.async { completion(.available(info)) }
            } else {
                DispatchQueue.main.async { completion(.upToDate) }
            }
        }.resume()
    }

    private static func version(_ a: String, isNewerThan b: String) -> Bool {
        let parse: (String) -> [Int] = { s in s.split(separator: ".").compactMap { Int($0) } }
        let av = parse(a), bv = parse(b)
        for i in 0..<max(av.count, bv.count) {
            let ai = i < av.count ? av[i] : 0
            let bi = i < bv.count ? bv[i] : 0
            if ai != bi { return ai > bi }
        }
        return false
    }
}
