<?php
// PHP grammar sample
class Greeter {
    public function hello(string $name): string {
        return "hello $name";
    }
}

echo (new Greeter())->hello("Volt");
