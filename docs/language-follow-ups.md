# Language Follow-Ups

- [x] Keep JSON diagnostics and syntax parsing behavior aligned.
  JSON diagnostics now share one token-level grammar validator across full-file analysis and large-file streaming diagnostics. The streaming path still reads huge files in bounded segments, but grammar decisions and diagnostic messages come from the shared validator.

- [ ] Tune JSON diagnostics recovery for malformed files.
  The streaming scanner currently keeps going after syntax errors, which can produce secondary diagnostics caused by the first real error. Improve recovery once real editing behavior shows which cases feel noisy.
