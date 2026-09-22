# Drawing workspace QA

Run on Windows with an authorized local DWG/DXF fixture:

```powershell
dotnet run --project tools/qa/drawing-workspace/DrawingWorkspaceQa.csproj -c Release -- <drawing-path> artifacts/qa/drawing-workspace
```

The harness imports individual CAD objects, renders collapsed/expanded drawing layers and directional selection offscreen, adds two artboards, and checks native save/load. It never opens or drives a desktop window. Generated images and the native project remain in the ignored artifacts directory. Do not commit private inputs or output files.
