// JavaScript grammar sample
const language = "JavaScript";
const values = [1, 2, 3];

function total(items) {
  return items.reduce((sum, value) => sum + value, 0);
}

console.log(`${language}: ${total(values)}`);
