import Foundation

// Swift grammar sample
struct Greeter {
    let name: String

    func hello(count: Int) -> String {
        return "hello \(name) \(count)"
    }
}

print(Greeter(name: "Swift").hello(count: 3))
