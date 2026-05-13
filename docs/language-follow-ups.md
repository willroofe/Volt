# Language Follow-Ups

- [ ] Keep JSON diagnostics and syntax parsing behavior aligned.
  The streaming JSON diagnostics scanner and the full JSON syntax parser are separate implementations. When JSON grammar behavior changes, update both paths so highlighting/tree behavior and diagnostics agree.

- [ ] Tune JSON diagnostics recovery for malformed files.
  The streaming scanner currently keeps going after syntax errors, which can produce secondary diagnostics caused by the first real error. Improve recovery once real editing behavior shows which cases feel noisy.
