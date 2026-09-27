# Morupixel 스튜디오 UI 디자인 시스템

편집기 화면을 고치거나 새 패널·다이얼로그를 만들 때 이 기준을 따른다. 값은 `src/App/Theme.cs`와 `src/UI/Theme.xaml`에 같은 이름으로 들어 있다. 두 파일의 값은 항상 함께 바꾼다.

## 원칙

1. **이미지가 주인공이다.** 화면에서 가장 밝고 채도가 높은 것은 편집 중인 이미지여야 한다. UI는 중성 회색 계열로 한 걸음 물러난다.
2. **표면 깊이로 구조를 보인다.** 바탕 위에 패널 카드가 뜨고, 버튼은 패널보다 밝게, 입력칸은 패널보다 어둡게 둔다. 선과 테두리는 이 구분을 보조할 때만 쓴다.
3. **파란색은 상태에만 쓴다.** 파우더 블루(Accent)는 선택·포커스·진행 표시, 블루(Primary)는 화면당 하나의 주 동작에만 쓴다. 장식에 쓰지 않는다.
4. **실무 밀도.** 오래 작업하는 화면이므로 행 높이와 여백을 줄이되, 명령 이름은 줄이지 않는다(`이름…` 같은 축약 금지).
5. **작은 글자는 선명하게.** UI 글꼴은 힌팅이 된 Segoe UI + 맑은 고딕을 정수 크기로 쓴다.

## 색 토큰

| 토큰 | 값 | 용도 |
| --- | --- | --- |
| `Canvas` | `#0C0D10` | 가장 어두운 층(예비) |
| `Stage` | `#15181D` | 문서 뒤 작업판, 활성 문서 탭 |
| `Header` | `#111317` | 창 바탕, 제목 표시줄, 상태 표시줄 |
| `Panel` | `#1C1F24` | 떠 있는 패널 카드, 다이얼로그 |
| `Surface` | `#2D323A` | 버튼, 선택된 세그먼트 |
| `Hover` / `Pressed` | `#373D46` / `#414853` | 상태 변화 |
| `Input` | `#101216` | 입력칸, 세그먼트 트랙(패널보다 어둡게) |
| `Line` | `#2A2F37` | 영역 사이 가는 선, 카드 테두리 |
| `Stroke` | `#3B414B` | 입력칸·컨트롤 외곽선 |
| `Text` | `#E8EBF0` | 본문 (Panel 대비 약 14:1) |
| `Muted` | `#A3ABB7` | 보조 라벨 (Panel 대비 약 7.5:1) |
| `Subtle` | `#6F7784` | 캡션·비활성 보조 정보 |
| `Accent` | `#A8CAFF` | 선택·포커스·진행 표시 |
| `Primary` / `PrimaryHover` | `#3A6FDB` / `#4A7FE9` | 주 동작 버튼 (흰 글자 대비 약 4.8:1) |
| `Selected` | `#243857` | 선택된 행·도구 배경 |
| `RowHover` | `#252930` | 목록 행 호버 |
| `Danger` / `Success` / `Warning` | `#F07178` / `#6FD49A` / `#E9C46A` | 오류·성공·주의 |

새 색이 필요하면 코드에 16진수를 직접 쓰지 말고 토큰을 먼저 추가한다.

## 글자

| 단계 | 크기 | 쓰임 |
| --- | --- | --- |
| `CaptionSize` | 12 | 캡션, 입력칸 위 라벨, 상태 표시줄 |
| `BodySize` | 13 | 본문, 버튼, 메뉴, 탭. 섹션 제목은 13 SemiBold |
| `HeadingSize` | 14 | 속성 패널의 대상 이름 |
| `TitleSize` | 18 | 다이얼로그·시작 화면 제목 |

- 글꼴: `Segoe UI, Malgun Gothic`. 번들된 Pretendard는 문서 텍스트용 리소스로 유지하되 작은 UI 글자에는 쓰지 않는다. 2026-09-24 오프스크린 A/B에서 12–13px Pretendard(CFF)가 맑은 고딕보다 획이 두껍고 흐리게 그려졌다. Preview 7에서 UI 글꼴을 바꾼 이유와 같다.
- 크기는 정수를 쓴다. 소수 크기는 힌팅 격자에서 벗어난다.

## 치수

- 간격: 4 · 8 · 12 · 16. 카드 사이와 창 가장자리는 8.
- 모서리: 카드 10, 입력칸·버튼 6, 작은 칩 4–5.
- 높이: 기본 버튼·입력칸 30(`ControlHeight`), 옵션 바·밀집 행 28(`CompactHeight`), 레이어 행 40, 인스펙터 동작 행 최소 34, 슬라이더 값 입력 26.
- 창 구조: 제목 표시줄(메뉴 포함) 44 · 도구 옵션 카드 44 · 본문 · 상태 표시줄 26. 도구막대 84, 오른쪽 패널 기본 396(324–520).

## 컴포넌트

| 이름 | 적용 | 모양 |
| --- | --- | --- |
| 기본 버튼 | `Button` 기본 스타일 | Surface 바탕, 테두리 없음, 호버·누름은 흰색 7%/13% 덧칠, 키보드 포커스는 바깥 2px Accent 링 |
| 고스트 버튼 | `Theme.Styled(b, "GhostButton")` | 투명, 호버 때만 표면. 제목 표시줄·보조 동작 |
| 주 버튼 | `"PrimaryButton"` | Primary 바탕, 흰 SemiBold. 화면당 하나 |
| 아이콘 버튼 | `Theme.IconButton(Theme.Glyphs.X, …)` | 투명 정사각형, 24단위 격자 선 아이콘(`Theme.Glyph`) |
| 도구 버튼 | `StyleToolButton` | 아이콘 버튼 34px, 선택 시 Selected 바탕 + Accent 테두리 |
| 패널 탭 | `"PanelTab"` | 글자만, 선택 시 Text SemiBold + 2px Accent 밑줄 |
| 세그먼트 | `"SegmentButton"` + Input 트랙 | 선택 조각만 Surface로 올라옴. 레이어 분류, 사진 편집/디자인, RGB/CMYK |
| 입력칸·콤보 | `TextBox`, `ComboBox` 기본 스타일 | Input 바탕 + Stroke 외곽선, 포커스 시 Accent |
| 체크박스 | `CheckBox` 기본 스타일 | 16px, 선택 시 Primary 바탕 + 흰 체크 |
| 슬라이더 | `Slider`, `"SpectrumSlider"` | 4px 트랙, Accent 채움, 14px 밝은 손잡이와 호버 후광 |
| 매개변수 슬라이더 | `ParameterSlider` | 한 줄에 라벨 · 초기화 · 이동 간격 · 값, 아래 슬라이더 |
| 인스펙터 동작 행 | `Theme.ActionRow` | 전체 너비, 줄 바꿈, 오른쪽 셰브론, 호버 시 표면 |
| 섹션 | `Theme.Section` | 가는 선 + 13 SemiBold 제목 |
| 카드 | `GlassPanel`, `ClipBorder` | Panel 바탕, Line 테두리, 모서리 10. `ClipBorder`는 내용을 둥근 모서리로 자른다 |
| 레이어 행 | `LayerRow` | 40px, 28px 썸네일, 이름 + 캡션 두 줄, 선택 시 Selected + 왼쪽 2px Accent 막대 |

## 패널 배치

- 오른쪽 위 카드는 히스토그램(사진 편집) · 탭 줄 · 현재 탭 내용이다. 오른쪽에 도킹된 탭 패널은 자기 머리글을 숨기고(`StudioPane.SetEmbedded`), 탭 줄 오른쪽 `⋯`가 같은 도킹 메뉴를 연다.
- 레이어 카드와, 왼쪽에 두거나 분리한 패널은 머리글(제목 · 개수 · 핀 · `⋯`)을 가진다. 제목을 끌면 분리된다.
- 캔버스와 오른쪽 패널 사이 8px 간격이 너비 조절 손잡이, 오른쪽 두 카드 사이 8px 간격이 높이 조절 손잡이다. 마우스를 올리면 선이 나타난다.

## 검증

화면을 고친 뒤에는 오프스크린 렌더로 비포/애프터를 비교한다. 창을 띄우지 않는다.

```
dotnet build src/Compositor.Windows.csproj -c Release
src\bin\Release\net8.0-windows10.0.19041.0\win-x64\Morupixel.exe --render-studio-previews <출력 폴더>
src\bin\Release\net8.0-windows10.0.19041.0\win-x64\Morupixel.exe --self-test <결과 파일>
```

`--render-studio-previews`는 시작·편집·색상·브러시·디자인 화면, 속성 패널, 다이얼로그와 함께 `window-1280x720`, `window-1366x768`, `window-1920x1080`, `design-1280x720` 창 크기 변형을 만든다. 호버·드래그·실제 DPI 배율은 오프스크린 렌더에 나타나지 않으므로 실제 실행으로 따로 확인한다.
