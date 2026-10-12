# Contributing

Thanks for wanting to help. Bug reports, ideas and pull requests are all welcome.

## Reporting bugs and asking for features

Open an [issue](https://github.com/hcornelison/gcs-file-viewer/issues/new/choose). For a bug, say what you did, what you expected and what happened, plus your operating system and how you signed in (gcloud CLI, Application Default Credentials or a key file). Never paste credentials, tokens or private bucket names.

Security problems go through the private process in [SECURITY.md](SECURITY.md), not public issues.

## Making a change

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). No Google Cloud account is needed to build or to run the tests.

```bash
dotnet build
dotnet test
```

For anything larger than a small fix, open an issue first so the approach can be agreed before you write it.

What a pull request needs:

- **It builds without warnings.** Warnings are errors here, and `.editorconfig` code style is enforced by the build.
- **It has tests.** View model and storage changes are tested against the in-memory fakes in `tests/GcsFileViewer.Tests`; see the README's Test section for the emulator tests.
- **It keeps the layering.** The README's project layout table shows which library may depend on which. Storage code must not reference the UI.
- **It updates the docs.** User-visible changes belong in `README.md` and under "Unreleased" in `CHANGELOG.md`.
- **Package versions go in `Directory.Packages.props`**, not in individual project files.

CI runs the build and all tests on every pull request.

## Licence

By contributing you agree that your contribution is released under the project's [MIT License](LICENSE).
