#!/usr/bin/env sh
# Shell grammar sample

name="${1:-Volt}"

greet() {
  printf 'hello %s\n' "$name"
}

if [ "$name" = "Volt" ]; then
  greet
fi
