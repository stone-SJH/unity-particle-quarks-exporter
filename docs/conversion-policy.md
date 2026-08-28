# Conversion Policy

Unknown input is never silently omitted. A known unsupported field may have a
documented fallback only when the manifest marks it as such. Strict mode fails
on unsupported, unknown, or target-incompatible behavior; best-effort mode may
emit a partial artifact but must return a non-ready status and a complete
diagnostic report.

Every approximation names its policy constants. For example, a trail lifetime
converted to update-history samples must state the chosen samples-per-second
constant and the resulting formula; it must not leave a bare numeric literal in
the mapping code.

