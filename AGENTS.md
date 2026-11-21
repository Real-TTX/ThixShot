# ThixShot

- Commit author and committer are Matthias Schmoldt. Use his configured Git identity. Keep commit attribution exclusive to him.
- Work on `dev` for nightlies and `main` for releases. When advancing both from the same commit, fast-forward the branches. The version number comes from the GitHub workflow run number.
- Follow the ThixClip desktop design. Use the shared MatDesktop window, title bar, navigation, typography, color tokens, control themes and light/dark support. Keep the files copied from MatDesign unchanged and make application-specific changes outside the toolkit.
- Keep business operations in static classes and enforce permissions there. Keep file and server access out of the presentation model.
