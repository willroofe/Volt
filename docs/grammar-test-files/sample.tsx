import React from "react";

type Props = {
  name: string;
  count?: number;
};

export function Badge({ name, count = 1 }: Props) {
  const label = `${name}: ${count}`;
  return <span data-count={count}>{label}</span>;
}
