import React from "react";

// JSX grammar sample
export function Greeting({ name }) {
  const count = 3;
  return <section data-count={count}>Hello, {name}</section>;
}
