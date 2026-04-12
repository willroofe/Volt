# Strip obsolete editor layout methods from MainWindow.xaml.cs (1-based line numbers 205-749).
from pathlib import Path

p = Path(__file__).resolve().parents[1] / "Volt" / "UI" / "MainWindow.xaml.cs"
lines = p.read_text(encoding="utf-8").splitlines(keepends=True)
# 0-based: keep 0..203, drop 204..748, keep 749..
new_lines = lines[:204] + lines[749:]
p.write_text("".join(new_lines), encoding="utf-8")
print("before", len(lines), "after", len(new_lines), "removed", len(lines) - len(new_lines))
