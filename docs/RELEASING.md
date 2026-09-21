# Releases

Application changes pushed to `main` become a GitHub preview after the build, editor self-tests, and packaged executable self-tests pass. Documentation-only and release-automation-only edits run CI without publishing another app package. Pull requests, other branches, and manual CI runs cannot publish.

Main runs are serialized. Changes are compared with the nearest published source commit, so app changes from a failed or replaced pending run are included in the next run, even if that next push only edits documentation.

The project version is used when available. If that version already belongs to an earlier commit, the workflow appends `.build.<run_number>`; a stable project version becomes `-preview.build.<run_number>`. Explicit `v<version>` tags still support deliberate stable releases, and must match the project version.

The release job uses write permission only after the read-only build passes. It creates a draft, uploads the ZIP and SHA-256 file, checks GitHub's ZIP digest against the checksum, and then publishes. The source commit is recorded in the release and verified against the tag. Existing files and tags are never replaced. A retry can finish its own draft or verify an already published release; conflicting or incomplete bytes stop publication for review.

The website selects the newest complete published version, including previews. README download links point to the official Releases page so they do not stay pinned to an old ZIP. Updating GitHub does not replace a copy already downloaded onto a user's PC.

Run `./scripts/TestReleaseAutomation.ps1` to check version selection and publication policy without contacting GitHub. Local app testing and packaging remain `./scripts/Test.ps1` and `./scripts/Publish.ps1`.
