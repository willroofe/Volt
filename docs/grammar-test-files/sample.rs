// Rust grammar sample
struct Greeter {
    name: String,
}

impl Greeter {
    fn hello(&self) -> String {
        format!("hello {}", self.name)
    }
}

fn main() {
    let greeter = Greeter { name: String::from("Rust") };
    println!("{}", greeter.hello());
}
