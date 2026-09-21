<img src="https://raw.githubusercontent.com/catsony5-web/compositor-windows/main/.github/media/morupixel.svg" width="44" height="44" alt="Morupixel">

# Morupixel

이미지에 집중하는 작은 작업실.

레이어, 색상, 합성을 위한 Windows 이미지 편집기입니다.

**Preview 17 · Windows 10 2004 이상 / Windows 11 x64**

[Windows 다운로드](https://github.com/catsony5-web/compositor-windows/releases) · [홈페이지](https://morupixel.arch-t.chatgpt.site/) · [릴리스](https://github.com/catsony5-web/compositor-windows/releases)

![푸른 바다의 빛과 파도 — Morupixel 브랜드 이미지](https://raw.githubusercontent.com/catsony5-web/compositor-windows/main/.github/media/ocean-wave.png)

- **사진과 디자인 함께하기** — 같은 문서에서 사진·문자·사각형·타원을 편집하고, 작업에 맞는 도구와 패널로 전환.
- **쌓고 합성하기** — 레이어 그룹, 혼합 모드, 마스크와 클리핑, 선택 레이어 이미지 출력.
- **빛과 색 다듬기** — 사진 현상·조정 레이어, 사용자 이미지 브러시, 선택한 색의 톤과 추천 조합.
- **배경 지우기** — 기기에서 처리하는 AI, 다시 수정할 수 있는 마스크.
- **파일 주고받기** — PDF·PDF 호환 AI·PSD/PSB·DWG/DXF 가져오기, PNG·JPEG·TIFF·PDF·PSD와 ICC 기반 CMYK TIFF 출력. [보존 범위와 제한](docs/FILE_COMPATIBILITY.md)

빈 작업 화면에서 **새 문서 / 열기 / 배우기**로 시작하세요. Preview 17은 문서를 닫을 때 이름과 저장 여부를 확인하는 어두운 테마의 안내창을 제공합니다. [이번 버전의 변경 사항](docs/RELEASE_NOTES.md)

<details>
<summary>시작하기</summary>

최신 릴리스에서 Windows x64용 ZIP 전체를 압축 해제하고 `Morupixel.exe`를 실행하세요. .NET 실행 환경과 AI 모델이 포함되어 있습니다.

AI 배경 제거에는 [Microsoft Visual C++ x64 런타임](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)이 필요합니다.

현재 개발 프리뷰이며 코드 서명은 적용 전입니다. 편집 작업 공간은 sRGB이며, CMYK는 프로필 변환 미리보기와 TIFF 출력으로 제공합니다. [지원 범위](docs/PORTING.md) · [인쇄 안내](docs/CMYK.md)

</details>

[사용·개발 안내](https://github.com/catsony5-web/compositor-windows/blob/main/docs/GUIDE.md) · [검증 기록](docs/VALIDATION.md) · [MIT 라이선스](LICENSE)

Robbie Tilton / Wonder Assembly LLC의 [Compositor](https://github.com/robbietilton/Compositor) 일부 코드와 알고리즘을 바탕으로 만든 독립 프로젝트입니다. 원작의 공식 Windows 배포판이 아닙니다. [원작 고지](NOTICE.md) · [함께 사용한 오픈소스](THIRD_PARTY_NOTICES.md)
