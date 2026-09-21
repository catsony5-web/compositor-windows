# ICC 프로필과 CMYK 인쇄용 출력

Morupixel의 CMYK 기능은 **인쇄용 ICC 프로필을 적용한 TIFF 출력과 색상 미리보기**를 위한 기능입니다. 편집 문서는 8비트 sRGB이며, 레이어와 작업 파일은 그대로 유지합니다.

## 사용 방법

1. CMYK 인쇄용 내보내기를 엽니다.
2. 인쇄소에서 받은 CMYK `.icc` 또는 `.icm` 파일을 선택합니다. 기본값은 Windows가 제공하는 CMYK 프로필입니다. 기본값이 모든 인쇄소나 용지에 적합하다는 뜻은 아닙니다.
3. sRGB 원본과 CMYK 프로필 변환 미리보기를 비교합니다.
4. DPI와 인쇄 크기를 확인하고 **CMYK TIFF 저장**을 누릅니다.

내보낸 TIFF는 실제 C·M·Y·K 네 채널과 선택한 ICC 프로필을 포함합니다. DPI는 인쇄 해상도 메타데이터이며 픽셀 개수를 바꾸지 않습니다. 투명한 부분은 흰색 배경과 합쳐서 출력합니다. 파일은 임시 파일에 완성한 뒤 교체하므로 변환이나 저장이 실패해도 기존 파일을 보존합니다.

## 색상 처리 범위

- 이미지 입력: 포함된 ICC 프로필을 이용해 sRGB로 변환합니다. 입력 이미지의 원래 색공간을 편집 문서의 작업 색공간으로 유지하는 방식은 아닙니다.
- CMYK 출력: Windows Imaging Component의 색상 변환을 사용해 sRGB에서 지정한 CMYK 출력 프로필로 변환합니다. RGB 프로필을 CMYK 프로필로 잘못 지정하면 오류를 표시합니다.
- 미리보기: 선택한 프로필로 CMYK 변환한 뒤 sRGB로 되돌린 결과입니다. 변환으로 달라지는 색을 비교할 수 있습니다.
- 미리보기에 종이색·잉크 농도 시뮬레이션, 모니터별 캘리브레이션, 렌더링 의도 선택, 별색 분판은 포함되지 않습니다. 실제 출력은 인쇄소·용지·장비에 맞는 프로필로 확인해야 합니다.
- CMYK 네 채널을 직접 브러시로 편집하거나 CMYK 상태의 레이어를 보존하는 문서 모드는 제공하지 않습니다.

출력 프로필이 잘못되었거나 읽을 수 없는 경우 다른 프로필로 조용히 대체하지 않습니다. 다른 프로필을 선택하거나 Windows 기본값으로 직접 돌아갈 수 있습니다. CMYK 기본 프로필이 없는 Windows 환경에서는 인쇄소 프로필을 선택해야 합니다.

## 원작과 차이

참조한 Compositor 소스는 입력 이미지를 sRGB로 변환하고 sRGB 프로젝트를 사용합니다. 프린터 ICC 프로필 선택, CMYK 문서·출력 기능은 이 스냅샷에서 제공하지 않습니다. Morupixel의 CMYK 출력은 이번에 별도로 구현한 확장입니다. 자세한 원작 비교는 [원작 기능 확인](UPSTREAM_FEATURES.ko.md)을 참고하세요.

## 의존성과 라이선스

변환 엔진은 Windows에 포함된 WIC/WCS를 사용합니다. Windows 시스템 ICC 프로필을 설치된 환경에서 읽으며, 그 파일을 Morupixel 배포물에 복사하거나 재배포하지 않습니다. 사용자가 선택한 인쇄소 프로필은 해당 파일의 이용 조건을 따릅니다.

WIC의 CMYK 픽셀 순서와 TIFF 인코더 지원은 [Microsoft의 기본 픽셀 형식 문서](https://learn.microsoft.com/en-us/windows/win32/wic/-wic-codec-native-pixel-formats)에 명시되어 있습니다.

## 개발 연결

```csharp
new CmykExportDialog(owner, document).ShowDialog();
CmykExport.Export(document, "output.tif", profilePath: null, dpi: 300);
Raster preview = CmykExport.Preview(raster, profilePath: null);
```

`profilePath: null`은 Windows 기본 CMYK 프로필을 의미합니다. `CmykExportTests.Run(Test)`는 실제 TIFF 재디코딩, CMYK 채널·내장 프로필·해상도, 색상 변환, 원본 불변성과 실패 시 파일 보존을 검증합니다.

2026-09-21 Windows 11의 독립 검증에서 CMYK 테스트 **7개 모두 통과**했습니다. 내장 ICC 바이트가 선택한 프로필과 완전히 일치했고, 불투명·반투명 색상 10종의 미리보기와 TIFF 재디코딩 결과가 채널값 1 이내로 일치했습니다. 출력 대화상자의 생성·비동기 미리보기·저장 버튼 활성화와 레이아웃도 데스크톱 창을 열지 않는 WPF 렌더링으로 확인했습니다. 이는 실제 인쇄기에서의 색상 교정 검증을 뜻하지 않습니다.
