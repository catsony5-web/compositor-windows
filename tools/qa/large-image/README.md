# Large image smoke check

Run from the repository root:

```powershell
dotnet run --project tools/qa/large-image/LargeImageQa.csproj -c Release -- artifacts/large-image-preview18
```

This opt-in check creates a 110 MP PNG, opens it through the editor offscreen,
renders the editor, and saves/reopens a native project while verifying pixels.
It requires several GiB of available memory. No desktop window is shown.
The ordinary self-tests use a smaller 20 MP regression fixture and narrow
65,535-pixel fixtures to test both dimension boundaries without this memory cost.
