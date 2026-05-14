# Language Follow-Ups

- [x] Keep JSON diagnostics and syntax parsing behavior aligned.
  JSON diagnostics now share one token-level grammar validator across full-file analysis and large-file streaming diagnostics. The streaming path still reads huge files in bounded segments, but grammar decisions and diagnostic messages come from the shared validator.

- [x] Tune JSON diagnostics recovery for malformed files.
  The shared JSON grammar validator now recovers from common missing-comma cases and ignored secondary container tokens, so malformed input reports the primary edit needed without cascades such as unexpected colons or braces.
