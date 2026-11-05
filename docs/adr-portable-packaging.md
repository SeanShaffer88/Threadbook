# ADR: Desktop storage selection and Windows packaging

Accepted for beta.2. Keep marker policy in one desktop startup module linked into a dependency-free console test host. Core/storage APIs stay unchanged: desktop composes storage selection with JsonStore. Explicit paths retain priority and existing behavior. Portable mode rejects filesystem links and has no recovery fallback into another library.

Use standard per-user Inno Setup with a stable AppId and explicit file entries, plus a complete framework-dependent portable ZIP. Native zero-dependency scripts reject existing destinations and unexpected payloads. Compilation, static inspection, real portable smoke and installed lifecycle are separate gates. CI does not install a compiler; local compiler qualification is recorded privately. This change has no application UI visual changes or custom installer theme.
