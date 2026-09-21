# Morupixel · 모루픽셀

**0.2.0-preview.1 · Windows x64 · 독립 이미지 편집기**

모루픽셀은 [Robbie Tilton / Wonder Assembly LLC의 Compositor](https://github.com/robbietilton/Compositor) 일부 코드와 알고리즘을 바탕으로 만든 Windows 이미지 편집기입니다. 독자적인 이름과 C#·WPF 구현을 사용하며 원작의 공식 Windows 제품이 아닙니다.

ZIP의 **모든 파일을 압축 해제한 뒤 `Morupixel.exe`를 실행**하세요. .NET 런타임과 로컬 AI 배경 제거 모델을 포함합니다. 사진 편집이나 AI 추론에 네트워크 연결이 필요하지 않습니다.

AI 배경 제거에는 [Microsoft Visual C++ x64 재배포 런타임](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)이 필요합니다. 이미 설치되어 있으면 그대로 사용하고, AI 엔진을 불러오지 못한다는 안내가 나오면 공식 최신 x64 패키지를 설치하세요. 이 ZIP에는 해당 시스템 런타임 설치 프로그램이 포함되지 않습니다.

[기능·호환성 비교](docs/PORTING.md) · [변경 사항](docs/RELEASE_NOTES.md) · [AI 모델 설명](docs/BACKGROUND_REMOVAL.md) · [원작 출처](NOTICE.md)

![Morupixel 편집 화면 — 실제 WPF 컨트롤의 오프스크린 렌더](docs/screenshots/editor.png)

## 편집 기능

| 영역 | 제공 기능 |
| --- | --- |
| 작업 공간 | 문서 탭 최대 8개, 확대/이동, 안내선·스냅, 히스토리 기반 실행 취소/다시 실행 |
| 레이어 | 레이어 그룹, 순서·이름·표시·잠금·불투명도, 14개 혼합 모드, 여러 레이어 선택·변형 |
| 변형 | 이동·회전·비균일 크기 조절·뒤집기, 네 꼭짓점 원근 왜곡 |
| 마스크 | 래스터 마스크, 그룹 마스크, 아래 레이어에 클리핑, 브러시로 수정 |
| 선택 | 사각·타원·올가미·다각형·마술봉, 추가·빼기·교차·반전, 페더·확장·축소, 알파 선택 |
| 그리기·보정 | 브러시·지우개·복제 도장·복구·스머지·액화·흐림 브러시, 도형·그라데이션, 내용 인식 채우기 |
| 텍스트 | 다시 편집 가능한 내용·글꼴·크기·굵게·기울임·정렬·색상 |
| 조정 레이어 | RGB 전체·빨강·초록·파랑 채널별 레벨·곡선, 색조/채도, 노출·오프셋·감마, 그라디언트 맵, 그레인 |
| 필터 | 가우시안·모션 흐림, 노이즈·렌즈 왜곡 등 |
| 배경 제거 | 번들 U²-NetP 모델을 Microsoft ONNX Runtime CPU로 실행, 결과를 수정 가능한 마스크로 적용 |
| 이미지 파일 | PNG/JPEG/BMP/TIFF/GIF, EXIF 방향 보정, 내장 ICC→sRGB 변환, Windows 코덱이 설치된 HEIC/HEIF |
| 내보내기 | PNG·TIFF·품질 조절 JPEG, 파일 크기·이미지 미리보기, 투명도 유지 또는 JPEG 흰색 배경 |
| 인쇄용 출력 | ICC 프로필을 포함한 CMYK TIFF, 프로필 변환 미리보기·DPI 설정 ([사용 안내](docs/CMYK.md)) |
| 작업 저장 | `.moruproj` 버전 2, 이전 `.cwproj` 버전 1/2 읽기, 원본 `.comp` 일부 기능 가져오기/내보내기 |

## 원작과의 차이

이 버전은 기능을 확장한 개발 프리뷰입니다. **원작과 모든 기능·화질·속도가 같다는 검증을 마친 제품은 아닙니다.** 세부적인 색 처리와 마스크·그룹 렌더링, 텍스트 줄바꿈, 복구 및 내용 인식 채우기 결과는 원작과 다를 수 있습니다.

- 그룹은 분리 합성 방식입니다. 원작의 패스스루 그룹과 혼합 결과가 다를 수 있습니다.
- 텍스트 문단 상자·자간·행간, 편집 가능한 벡터 도형, 색상 범위별 HSV, 일부 레이어 효과·라이브/연결 해제 마스크는 미지원입니다.
- `.comp` 상호 변환은 지원 부분만 제공합니다. 지원하지 않는 의미를 가진 파일은 임의로 평탄화하지 않고 오류와 이유를 표시합니다. 텍스트를 수정할 때 OS별 글꼴 차이가 발생할 수 있습니다.
- AI 배경 제거는 320×320 U²-NetP를 사용합니다. 머리카락·털·투명 물체·복잡한 배경은 수동 마스크 수정이 필요할 수 있습니다. Apple Vision과 같은 모델이나 결과는 아닙니다.
- PSD, CMYK 편집, 16/32비트 작업 공간, 원본 ICC/PPI 메타데이터 보존, HEIC 내보내기는 미지원입니다. GIF/TIFF 등 다중 프레임 파일은 첫 프레임을 사용합니다.
- 한 변 8,192px, 총 16,777,216픽셀, 최대 128개 레이어와 레이어 픽셀/마스크 합계 384MB 제한이 있습니다. 실행 취소는 50개 항목과 별도 보관 픽셀 192MB 범위입니다.

## 빌드와 검증

Windows와 .NET 8 SDK가 필요합니다. 첫 복원 시 공식 NuGet에서 ONNX Runtime을 받습니다. 모델은 `models/`에 체크섬과 라이선스를 포함해 보관합니다.

```powershell
.\scripts\Build.ps1
.\scripts\Test.ps1
.\scripts\Publish.ps1 -Version 0.2.0-preview.1
```

배포 파일은 `release\Morupixel-0.2.0-preview.1-win-x64.zip`입니다. 패키징 스크립트는 모델 SHA-256을 확인하고 실제 배포 EXE의 자동 테스트도 실행합니다.

```powershell
.\Morupixel.exe --self-test .\self-test.txt
.\Morupixel.exe --render-preview .\preview.png
```

`--render-preview`는 창을 표시하지 않고 WPF 화면 레이아웃을 이미지로 그리는 개발 검증입니다. 실제 마우스/키보드 조작 검증을 대체하지 않습니다. [검증 기록](docs/VALIDATION.md)은 해당 버전과 검증 방식을 구분합니다.

원작 MIT 고지는 [LICENSE](LICENSE), 제품 관계는 [NOTICE.md](NOTICE.md), 의존성·모델 고지는 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)에 있습니다. 저장소 주소에 남아 있는 `compositor-windows`와 내부 네임스페이스는 이전 버전의 개발 식별자이며 제품명은 Morupixel입니다.
