# Python grammar sample
from dataclasses import dataclass

@dataclass
class Greeting:
    name: str = "Python"

def render(value: Greeting) -> str:
    return f"hello {value.name}"

print(render(Greeting()))
