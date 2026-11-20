import Foundation

typealias J = [String: Any]

struct ServerError: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// The local server's /panel/api. Signs in with ~/.udriver/app.key, or with a panel ticket while no app key exists.
@MainActor final class API {
    static let shared = API()

    let env = ProcessInfo.processInfo.environment
    lazy var home = env["HOME"] ?? NSHomeDirectory()
    lazy var port = Int(env["UDRIVER_HTTP_PORT"] ?? "") ?? 8787
    var base: String { "http://127.0.0.1:\(port)" }
    var dll: String { env["UDRIVER_SERVER_DLL"] ?? home + "/.udriver/server/udriver-mcp.dll" }

    private let session = URLSession(configuration: .ephemeral)
    private var bearer: String?
    private var csrf = ""
    private var signedIn = false

    private func read(_ name: String) -> String? {
        (try? String(contentsOfFile: home + "/.udriver/" + name, encoding: .utf8))?
            .trimmingCharacters(in: .whitespacesAndNewlines).nilIfEmpty
    }

    private func signIn() async throws {
        if let key = read("app.key") { bearer = key; signedIn = true; return }
        guard let key = read("panel.key") else { throw ServerError(message: "UDriver has not finished starting.") }
        var r = URLRequest(url: URL(string: base + "/panel/ticket")!)
        r.httpMethod = "POST"
        r.httpBody = Data(key.utf8)
        let ticket = String(decoding: try await session.data(for: r).0, as: UTF8.self)
        _ = try await session.data(from: URL(string: base + "/panel?ticket=" + ticket)!)
        let page = String(decoding: try await session.data(from: URL(string: base + "/panel?remote")!).0, as: UTF8.self)
        csrf = page.components(separatedBy: "data-csrf=\"").dropFirst().first.map { String($0.prefix { $0 != "\"" }) } ?? ""
        signedIn = true
    }

    func data(_ path: String, _ form: [String: String]? = nil, timeout: TimeInterval = 20, again: Bool = true) async throws -> (Data, Int) {
        if !signedIn { try await signIn() }
        var r = URLRequest(url: URL(string: base + path)!)
        r.timeoutInterval = timeout
        if let bearer { r.setValue("Bearer " + bearer, forHTTPHeaderField: "Authorization") }
        if var form {
            if bearer == nil { form["csrf"] = csrf }
            r.httpMethod = "POST"
            r.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")
            let safe = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "-._~"))
            r.httpBody = Data(form.map { "\($0.key)=\($0.value.addingPercentEncoding(withAllowedCharacters: safe) ?? "")" }.joined(separator: "&").utf8)
        }
        let (d, resp) = try await session.data(for: r)
        let status = (resp as? HTTPURLResponse)?.statusCode ?? 0
        if (status == 401 || status == 403) && again {
            signedIn = false; bearer = nil
            return try await data(path, form, timeout: timeout, again: false)
        }
        return (d, status)
    }

    @discardableResult
    func json(_ path: String, _ form: [String: String]? = nil, timeout: TimeInterval = 20) async throws -> J {
        let (d, _) = try await data(path, form, timeout: timeout)
        guard let j = (try? JSONSerialization.jsonObject(with: d)) as? J else {
            throw ServerError(message: String(decoding: d.prefix(200), as: UTF8.self))
        }
        if j["ok"] as? Bool == false { throw ServerError(message: j["error"] as? String ?? "Something went wrong.") }
        return j
    }

    func running() async -> Bool {
        var r = URLRequest(url: URL(string: base + "/panel")!)
        r.timeoutInterval = 1
        return (try? await session.data(for: r)) != nil
    }

    /// How to run the server: the one inside the app, else the installed one with .NET 10.
    func command() -> (String, [String])? {
        let fm = FileManager.default
        let bundled = Bundle.main.bundleURL.appendingPathComponent("Contents/MacOS/server/udriver-mcp").path
        if env["UDRIVER_SERVER_DLL"] == nil, fm.isExecutableFile(atPath: bundled) { return (bundled, []) }
        guard fm.fileExists(atPath: dll) else { return nil }
        let candidates = [env["DOTNET_ROOT"].map { $0 + "/dotnet" }, home + "/.udriver/dotnet/dotnet", home + "/.dotnet/dotnet",
                          "/usr/local/share/dotnet/dotnet", "/opt/homebrew/bin/dotnet", "/usr/local/bin/dotnet"]
        return candidates.compactMap({ $0 }).first(where: fm.isExecutableFile).map { ($0, [dll]) }
    }

    /// Starts the server when nothing answers on the port; returns an error to show, or nil once it answers.
    func ensureServer() async -> String? {
        if await running() { return nil }
        let fm = FileManager.default
        guard let (file, args) = command() else {
            return fm.fileExists(atPath: dll) ? "UDriver needs .NET 10. Run the UDriver installer once, then open this app again."
                : "UDriver is not installed on this Mac yet. Run the UDriver installer once, then open this app again."
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: file)
        p.arguments = args
        var e = env
        e["UDRIVER_HTTP_PORT"] = String(port)
        e["UDRIVER_PANEL"] = "1"
        e["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
        e["DOTNET_NOLOGO"] = "1"
        if e["UDRIVER_DIST"] == nil, fm.fileExists(atPath: home + "/.udriver/package") { e["UDRIVER_DIST"] = home + "/.udriver/package" }
        p.environment = e
        p.standardInput = FileHandle.nullDevice
        p.standardOutput = FileHandle.nullDevice
        try? fm.createDirectory(atPath: home + "/.udriver/logs", withIntermediateDirectories: true)
        fm.createFile(atPath: home + "/.udriver/logs/server.log", contents: nil)
        p.standardError = FileHandle(forWritingAtPath: home + "/.udriver/logs/server.log")
        do { try p.run() } catch { return "UDriver could not start: \(error.localizedDescription)" }
        for _ in 0..<100 {
            try? await Task.sleep(for: .milliseconds(200))
            if await running() { return nil }
            if !p.isRunning { return "UDriver stopped right after starting. Details are in ~/.udriver/logs/server.log." }
        }
        return "UDriver is taking too long to start."
    }
}

extension String {
    var nilIfEmpty: String? { isEmpty ? nil : self }
}

extension Dictionary where Key == String, Value == Any {
    func s(_ k: String) -> String {
        guard let v = self[k], !(v is NSNull) else { return "" }
        return v as? String ?? "\(v)"
    }
    func list(_ k: String) -> [J] { self[k] as? [J] ?? [] }
}
