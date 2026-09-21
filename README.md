# Compositor for Windows

**0.1.0-preview.2 · Windows x64 · 비공식 커뮤니티 포트**

Compositor for Windows는 [Robbie Tilton / Wonder Assembly LLC의 Compositor](https://github.com/robbietilton/Compositor)를 참고해 만든 독립적인 Windows용 커뮤니티 포트입니다. 원작자 또는 Wonder Assembly LLC가 이 포트를 보증하거나 배포하는 프로젝트가 아닙니다.

[Windows ZIP 다운로드](https://github.com/catsony5-web/compositor-windows/releases/tag/v0.1.0-preview.2) · [원작](https://github.com/robbietilton/Compositor) · [지원 범위](docs/PORTING.md) · [릴리스 안내](docs/RELEASE_NOTES.md)

Windows x64용 ZIP의 **전체 파일을 압축 해제한 다음 `Compositor.Windows.exe`를 실행**하세요. .NET 런타임을 포함하므로 별도 런타임 설치가 필요하지 않습니다. 코드 서명이 없는 초기 프리뷰입니다.

![Windows에서 실행한 Compositor 편집 화면](docs/screenshots/editor.png)

Preview 2 소스와 배포 EXE의 자동 테스트가 각각 46개 모두 통과했습니다. Windows 11에서 캔버스와 레이어 목록의 선택 동작도 확인했습니다. Preview 2 검증 결과는 [선택 수정 검증 기록](docs/SELECTION_FIX.md), Preview 1의 배포 검증 이력은 [기존 검증 기록](docs/VALIDATION.md)에 있습니다. [X·Instagram·Threads 공유 문안](docs/SOCIAL_POSTS.ko.md)도 제공합니다.

## 한국어 안내

이 포트는 .NET 8 WPF로 작성한 단일 문서 이미지 편집기 미리보기입니다. 레이어를 보존하는 Windows 전용 `.cwproj` 작업 파일을 저장하고 PNG/JPEG로 결과를 내보낼 수 있습니다. 원작의 macOS `.comp` 파일을 열거나 저장하는 호환 계층은 제공하지 않습니다.

### 현재 소스에서 확인되는 기능

| 영역 | 0.1.0 Preview에서 제공되는 범위 |
| --- | --- |
| 캔버스 | 새 캔버스, 캔버스 크기, 비율을 유지한 이미지 크기 조정, 선택 영역으로 자르기 |
| 레이어 | 추가, 목록 행 전체 선택, 이동 도구의 캔버스 레이어 자동 선택, 이름 변경, 복제, 삭제, 순서 변경, 표시/숨김, 잠금, 최대 32개 |
| 레이어 외관 | 불투명도와 Normal, Multiply, Screen, Overlay, Soft Light, Darken, Lighten, Difference, Color Dodge, Color Burn |
| 변형 | 이동, 수치 입력, 배율, 회전, 가로/세로 뒤집기 |
| 마스크 | 레이어별 8비트 마스크, 흰색 표시/검정 숨김, 추가·반전·제거, 브러시/채우기 편집 |
| 선택 | 사각형·타원 영역 선택, 전체 선택, 선택 해제, 선택 영역 제한 편집 |
| 그리기 | 브러시, 지우개, 브러시 크기·경도·농도, 전경색 선택, 사각형·타원 도형, 전경색→투명 그라데이션 |
| 문자·색상 | 클릭해 텍스트를 래스터 레이어로 추가, 색상 추출, 합성 이미지 복사 및 이미지 붙여넣기 |
| 보정 | Levels, Exposure, Saturation, Grayscale, Invert, Gaussian Blur |
| 파일 | PNG/JPEG/BMP/TIFF/GIF 열기·가져오기, `.cwproj` 저장/열기, PNG/JPEG 내보내기 |
| 실행 취소 | 불변에 가까운 문서 스냅샷 기반 Undo/Redo, 원자적 작업 파일 저장; 과거 최대 50개·기록 전용 픽셀 192MB |

### 레이어 선택 방법

이동 도구에서 **자동 선택**을 켜면 캔버스의 보이는 픽셀을 클릭해 가장 위의 레이어를 선택합니다. 투명한 부분은 통과하며, 잠긴 레이어도 선택할 수 있지만 잠금을 풀기 전에는 이동할 수 없습니다. 자동 선택을 끄면 캔버스를 클릭해도 레이어 목록에서 선택한 레이어가 유지됩니다. 레이어 목록에서는 썸네일, 이름, 행의 빈 공간을 클릭해 선택할 수 있고, 표시 체크박스나 잠금 버튼만 조작하면 현재 선택은 바뀌지 않습니다.

여기서 **자동 선택은 캔버스에서 레이어를 고르는 기능**입니다. 색이나 영역을 자동으로 선택하는 마법봉 도구는 아직 제공하지 않습니다.

### 원작 기능과의 차이 및 제한

이 버전에는 원작의 폴더/그룹, clipping mask, adjustment layer, lasso·polygon lasso·magic wand, content-aware fill, spot healing, clone stamp, curves, gradient map, grain/noise, motion blur, lens correction, background removal, 다중 문서 탭, snapping/guides, HEIC 입력, PSD 입력이 포함되어 있지 않습니다. 이동 도구의 자동 선택은 레이어 picking이며 magic wand가 아닙니다. 텍스트는 편집 가능한 원작 텍스트 메타데이터가 아니라 추가 시 래스터화됩니다.

`.cwproj`와 원작 `.comp`는 서로 다른 형식이므로 원작 작업 파일 호환을 약속하지 않습니다. EXIF 자동 회전, ICC 색상 관리, 인쇄용 CMYK도 지원 범위에 포함되지 않습니다. AI 배경 제거 또는 기타 AI 기능은 제공하지 않으며, 프로그램은 네트워크 연결 없이 로컬 편집을 전제로 합니다.

크기 제한은 한 변 최대 8,192px, 총 최대 16,777,216픽셀, 최대 32개 레이어입니다. 큰 이미지의 편집 성능, 소수 픽셀 이동·회전 경계의 안티앨리어싱은 개선이 필요합니다. 흐림 효과는 레이어 경계를 확장하지 않습니다.

실행 취소 기록은 소스에서 최대 50개 과거 항목과 기록이 독점적으로 보유하는 픽셀 데이터 192MB 범위에서 정리됩니다. 이 값도 원작의 기록 한도와 동일하다고 볼 수 없습니다.

## 빌드

소스 빌드에는 Windows와 .NET 8 SDK가 필요합니다. 프로젝트는 `net8.0-windows`와 WPF를 사용합니다.

```powershell
dotnet restore .\src\Compositor.Windows.csproj
dotnet build .\src\Compositor.Windows.csproj -c Release
```

다음 스크립트는 빌드, 자동 테스트, 런타임을 포함한 Windows x64 ZIP 생성을 수행합니다. 게시된 EXE에도 자동 테스트를 실행하고 SHA-256 체크섬을 생성합니다.

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1 -Version 0.1.0-preview.2
```

`Publish.ps1`는 `win-x64`와 `--self-contained true`로 게시하고 `release\Compositor.Windows-<버전>-win-x64.zip` 및 SHA-256 파일을 만듭니다. 별도 스크립트를 쓰지 않으면 다음처럼 .NET 8 SDK에서 직접 빌드할 수 있습니다.

```powershell
dotnet restore .\src\Compositor.Windows.csproj
dotnet build .\src\Compositor.Windows.csproj -c Release --no-restore
```

실행 시 이미지 또는 `.cwproj` 경로를 첫 번째 인자로 넘길 수 있습니다. `--self-test <결과 파일>`로 자동 테스트를 직접 실행할 수도 있습니다. Windows 10 및 새 PC에서의 별도 호환성 검증은 아직 수행하지 않았습니다.

## 작업 파일

`.cwproj`는 이 포트의 버전 1 형식입니다. ZIP 안에 `document.json`, `layers/<순번>.png`, 선택적인 `layers/<순번>.mask`를 둡니다. 이는 원작의 `.comp` 패키지와 다른 형식이며, 형식 버전 1만 읽습니다. 자세한 포팅 범위와 원작 알고리즘 출처는 [`docs/PORTING.md`](docs/PORTING.md)를 참고하세요.

## 라이선스 및 출처

원작의 MIT 고지는 [LICENSE](LICENSE)에 보존되어 있습니다. 원작 출처와 포트의 성격은 [NOTICE.md](NOTICE.md), 의존성별 고지는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)에 기록되어 있습니다. 이 문서의 기능 목록은 현재 소스 기준이며, 원작의 전체 기능 목록이나 동등성 선언이 아닙니다.

## Short English

Compositor for Windows 0.1.0 Preview 2 is an independent, unofficial Windows community port inspired by [Compositor by Robbie Tilton / Wonder Assembly LLC](https://github.com/robbietilton/Compositor). It is not endorsed by the original author or company.

The WPF/.NET 8 app provides raster layers, masks, transforms, selections, brush/eraser, shapes, text rasterization, basic adjustments, `.cwproj` project storage, and PNG/JPEG export. Preview 2 adds canvas layer picking and clearer layer-row selection without changing visibility when selecting. “Auto select” picks a layer from visible canvas pixels; it is not a magic-wand selection tool. This early preview has a smaller feature set than the macOS app; AI features and PSD/`.comp` compatibility are unsupported. Download the Windows x64 ZIP from [Releases](https://github.com/catsony5-web/compositor-windows/releases/tag/v0.1.0-preview.2), extract all files, and run `Compositor.Windows.exe`.

Build from source with the .NET 8 SDK using `.\scripts\Build.ps1` or `dotnet build .\src\Compositor.Windows.csproj -c Release`. See [`docs/PORTING.md`](docs/PORTING.md) for the compatibility matrix and algorithm attribution.
