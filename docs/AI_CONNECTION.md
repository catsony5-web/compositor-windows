# AI 연결

현재 소스의 **AI 명령 규약 8**에 대한 안내입니다. 공개 ZIP과 같은 버전의 실행 파일을 사용하고, 연결 후 `get_capabilities`로 실행 중인 편집기가 실제로 지원하는 기능을 확인하세요.

Morupixel을 Codex나 Claude Code 같은 외부 AI 도구에 연결하면 문서를 만들고, 이미지·텍스트·도형을 배치하고, 재료를 영역에 적용하고, 결과를 저장할 수 있습니다. 연결한 AI가 사용자의 요청을 해석하고 Morupixel의 편집 도구를 호출합니다. **Morupixel에는 API 키를 입력하지 않습니다.** AI 서비스의 로그인·모델 설정은 연결하는 프로그램에서 관리합니다.

## 연결해서 사용하기

1. 이 기능이 포함된 Morupixel을 실행하고 **AI 연결 → 로컬 연결 켜기**를 선택합니다.
2. AI 프로그램에 아래 MCP 서버를 등록합니다. **AI 연결 → 연결 설정…**에서도 실행 파일 경로가 들어간 설정을 복사할 수 있습니다.
3. AI에게 “Morupixel의 열린 문서를 확인하고, 현재 문서에 수정 가능한 제목을 넣어줘”처럼 요청합니다.

**연결 설정…** 창의 **연결 테스트**는 AI 프로그램과 같은 로컬 통로로 `get_state`·`get_capabilities`를 보내 문서 수, 명령 수, 앱 버전을 보여 줍니다. 같은 창에 요청 예시 문장도 있습니다.

AI는 `morupixel_list_sessions`로 연결된 창을 찾고, `morupixel_get_capabilities`로 기능을 확인합니다. `morupixel_get_state`에 `includeLayers: false`를 전달해 문서 목록과 변경 상태를 읽은 다음 필요한 객체만 조회합니다. 여러 Morupixel 창이 있으면 대상 세션을 구분해야 합니다. 연결을 끝내려면 **AI 연결 → 로컬 연결 켜기**를 다시 선택해 체크를 해제합니다.

MCP 클라이언트의 일반적인 JSON 설정입니다. `command`를 실제 `Morupixel.exe`의 절대 경로로 바꾸세요.

```json
{
  "mcpServers": {
    "morupixel": {
      "command": "C:\\Apps\\Morupixel\\Morupixel.exe",
      "args": ["--mcp"]
    }
  }
}
```

`--mcp`는 AI 프로그램이 실행하는 표준 입출력 서버입니다. 편집 창을 새로 띄우거나 이미 열린 창의 연결을 자동으로 켜지 않습니다. 같은 PC에서 사용자가 연결을 켠 Morupixel 세션을 찾아 명령을 전달합니다.

### Codex 설정

같은 Windows PC의 PowerShell에서 실행 파일 경로를 바꿔 등록합니다. **AI 연결 → 연결 설정… → Codex**에서도 실제 경로를 포함한 명령을 복사할 수 있습니다. [OpenAI 공식 MCP 안내](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)

```powershell
codex mcp add morupixel -- 'C:\Apps\Morupixel\Morupixel.exe' --mcp
codex mcp list
```

Codex의 사용자 `~/.codex/config.toml` 또는 신뢰한 프로젝트의 `.codex/config.toml`에 다음 항목을 등록할 수도 있습니다. 기존 설정에 병합하고, 이미 같은 서버 이름이 있으면 그 항목을 수정하세요. Windows 경로의 역슬래시는 큰따옴표 TOML 문자열 안에서 `\\`로 적습니다.

```toml
[mcp_servers.morupixel]
command = "C:\\Apps\\Morupixel\\Morupixel.exe"
args = ["--mcp"]
```

등록 후 사용하는 Codex 클라이언트에서 서버 연결을 새로고침하거나 재시작하세요. 이 설정은 Morupixel이 실행되는 Windows 호스트에 적용합니다. 웹이나 다른 PC의 AI가 이 로컬 실행 파일에 직접 접근하는 설정은 아닙니다.

### Claude Code 설정

같은 Windows PC의 PowerShell에서 다음처럼 사용자 범위로 등록합니다. **AI 연결 → 연결 설정… → Claude Code**에서도 복사할 수 있습니다. [Anthropic 공식 MCP 안내](https://code.claude.com/docs/en/mcp)

```powershell
claude mcp add --transport stdio --scope user morupixel -- 'C:\Apps\Morupixel\Morupixel.exe' --mcp
claude mcp get morupixel
```

Claude Code에서 `/mcp`로 연결 상태를 확인합니다. 기존에 같은 이름의 서버가 있다면 그 설정을 갱신하세요. 각 사용자는 자기 PC에 공개 Morupixel을 설치하고 자기 AI 프로그램에 연결합니다. WSL·원격 호스트는 이 Windows 로컬 연결 안내의 검증 범위에 포함하지 않습니다.

MCP는 편집 도구 연결이며 이미지 생성 구독이나 API 사용 권한을 전달하지 않습니다. 재료 이미지는 연결한 AI가 지원하는 이미지 생성 도구로 준비하거나 기존 파일을 사용합니다. 생성 모델·로그인·과금은 해당 제공자의 지원 범위를 따릅니다.

## 할 수 있는 작업

현재 MCP는 다음 **37개 도구**를 제공합니다. CLI에서는 앞의 `morupixel_`를 뺀 명령 이름을 사용합니다.

| 작업 | MCP 도구 |
| --- | --- |
| 세션·문서·지원 기능 확인 | `morupixel_list_sessions`, `morupixel_get_state`, `morupixel_get_capabilities` |
| 객체 검색·상세 조회 | `morupixel_query_layers`, `morupixel_get_layer` |
| 문서 만들기·열기·전환 | `morupixel_new_document`, `morupixel_inspect_file`, `morupixel_open_document`, `morupixel_activate_document` |
| 대지 만들기·수정·삭제 | `morupixel_add_artboard`, `morupixel_update_artboard`, `morupixel_delete_artboard` |
| 이미지·수정 가능한 문자·도형 | `morupixel_add_image`, `morupixel_add_text`, `morupixel_update_text`, `morupixel_add_shape` |
| 레이어 속성·삭제·순서 | `morupixel_set_layer`, `morupixel_delete_layer`, `morupixel_reorder_layer` |
| 조정 레이어·AI 배경 제거 | `morupixel_add_adjustment`, `morupixel_remove_background` |
| 프로젝트 저장·이미지 출력·미리보기 | `morupixel_save_project`, `morupixel_export_image`, `morupixel_preview` |
| PDF·.psd·.ai 내보내기(레이어 유지·합치기) | `morupixel_export_document` |
| 실행 취소·다시 실행 | `morupixel_undo`, `morupixel_redo` |
| 묶음 편집·사전 검증 | `morupixel_apply_batch` |
| 재료 등록·조회 | `morupixel_register_material`, `morupixel_query_materials`, `morupixel_query_patterns`(기본 해치 패턴) |
| 적용 영역 등록·조회 | `morupixel_define_region`, `morupixel_query_regions` |
| 재료 적용·패턴 변경 | `morupixel_apply_material`, `morupixel_update_material` |
| 디자인 스타일 조회·적용 | `morupixel_query_styles`, `morupixel_apply_style` |

기본 해치 패턴 19종은 `query_patterns`로 조회합니다(문서 없이 사용, `surface`: general·wall·floor·ground 순서, `nameContains`). 응답의 `materialId`를 `apply_material`·`update_material`에 넘기면 문서 재료 라이브러리에 자동 등록되며(가득 차면 `capacity_exceeded`), 패턴은 `ink`(`#RRGGBB`, 알파 01~FF의 `#AARRGGBB`, `default`; 완전히 투명한 잉크는 `invalid_arguments`)와 `lineWeight`(0.1~8, 이미지 재료는 무시)로 조절합니다. `update_material`은 생략한 값을 유지하고 `ink: "default"`는 기본 잉크로 되돌립니다. 재료 조회 결과에는 `kind`(`pattern`·`image`)·`patternId`, 맵핑에는 `ink`·`lineWeight`·`patternId`·`rendering`(`pattern_redrawn`·`image_tile`)이 들어갑니다. 패턴은 화면·출력 해상도에 맞춰 선을 다시 그리며, `export_image`의 `scale`이 1이 아니면 출력 크기로 다시 그려 내보냅니다. 없는 패턴 ID는 `material_not_found`이며 `query_patterns`로 올바른 ID를 찾습니다. [해치 패턴 안내](MATERIAL_MAPPING.md#해치-패턴)

### 해치 패턴 넣기

해치 패턴은 `apply_material` 한 번으로 넣을 수 있습니다. 재료는 `patternId`(`query_patterns`의 키, 예: `brick`) 또는 `materialId`로, 경계는 다음 중 **하나**로 지정합니다.

- `regionId`: `define_region`으로 등록한 영역(현재 선택 영역은 `define_region`의 `source: "selection"`으로 먼저 등록)
- `points`(+ 선택 `holes`): 문서 픽셀 기준 다각형. 같은 단계에서 영역 템플릿으로 저장되며 이름은 `regionName`(기본 `영역 N`)
- `boundaryLayerId`: 닫힌 도형이나 닫힌 CAD 경로 레이어(`define_region`의 `closed_layer`와 같은 규칙)

크기는 앱 속성 창과 같은 상대값 `scale`(크기 %/100, 0.1~10, 1이 기본 크기)·`verticalRatio`(세로 비율 %/100, 0.25~4)로 주거나, 이전처럼 픽셀 단위 `tileWidth`·`tileHeight`로 줍니다(`scale`과 `tileWidth`, `verticalRatio`와 `tileHeight`는 함께 쓸 수 없음). 생략하면 기본 크기·비율입니다. 회전 `angle`, 위치 `offsetX`·`offsetY`, 선 굵기 `lineWeight`, 잉크 `ink`, `opacity`·`blend`도 같은 호출에서 지정합니다. 결과에는 새 `layerId`와 사용한 `regionId`가 들어갑니다.

```json
{ "command": "apply_material", "arguments": { "documentId": "<documentId>", "expectedRevision": "<revision>",
  "patternId": "brick", "points": [{"x":40,"y":40},{"x":300,"y":40},{"x":300,"y":200},{"x":40,"y":200}],
  "regionName": "외벽", "scale": 0.6, "verticalRatio": 1.5, "angle": 15, "lineWeight": 1.2, "ink": "#FF5A3A2A", "opacity": 0.9 } }
```

`update_material`은 같은 인자(`patternId`·`materialId`, `scale`·`verticalRatio`·`tileWidth`·`tileHeight`, `angle`, `offsetX`·`offsetY`, `ink`, `lineWeight`, `background`, `opacity`, `blend`, `name`)로 기존 해치 레이어를 고칩니다. 지정하지 않은 값은 유지합니다(`verticalRatio`만 바꾸면 반복 너비 유지, `scale`만 바꾸면 세로 비율 유지). 경계와 레이어 변형은 그대로입니다. `get_layer`의 `material`에는 `patternId`, `patternKind`(`builtin`·`custom`), `scale`, `verticalRatio`, `angle`, `ink`, `lineWeight`, `background`가 들어가 바꾼 값을 그대로 확인할 수 있습니다. 두 명령 모두 `apply_batch` 단계로 쓸 수 있어, 도형을 만들고(`"ref": "room"`) 그 도형을 경계로(`"boundaryLayerId": "@room"`) 패턴을 채운 다음(`"ref": "lawn"`) `update_material`·`set_layer`의 `"layerId": "@lawn"`으로 조정하는 작업을 실행 취소 한 번으로 되돌릴 수 있습니다.

**바탕색.** `background`(`#RRGGBB`, `#AARRGGBB`, `none`)는 패턴 선 아래 경계 안을 칠합니다(앱의 **바탕색** 행과 같음). 기본값은 `none`(투명)이고 알파 `00`도 `none`입니다. 아래 레이어와는 선처럼 곱하기로 섞입니다. 이미지 재료에는 값만 보관되고 칠하지 않습니다(잉크와 같음).

**내 패턴(이미지로 만든 선 패턴).** `register_material`에 `kind: "line_pattern"`을 주면 앱의 **이미지로 패턴 추가…**와 같은 방식으로 이미지의 어두운 선을 잉크로, 밝은 바탕을 투명으로 바꿔 문서 재료 라이브러리에 등록합니다. `threshold`(0~1, 생략 시 앱이 제안하는 자동 기준; 낮출수록 선을 더 많이 찾음)와 `trim`(빈 여백 자르기, 기본 false)을 받습니다. 기본은 **이 문서에만** 등록하며, `saveToMyPatterns: true`일 때만 사용자의 **내 패턴** 목록(이 PC)에도 저장합니다(창 없이 실행한 `--automation-headless` 세션은 내 패턴 목록을 메모리에만 둡니다). `source`·`tileable`은 `kind: "image"`(기본) 전용이고, 선을 찾지 못하거나 대비가 부족한 이미지는 이유와 함께 `pattern_conversion_failed`입니다. 결과에는 `patternId: "custom:<32자리 16진수>"`, `kind: "pattern"`, `patternKind: "custom"`, 사용한 `threshold`, `savedToMyPatterns`가 들어갑니다.

`query_patterns`는 기본 패턴 뒤에 내 패턴 목록과 문서에 들어 있는 사용자 패턴을 이어서 돌려줍니다(`kind`: `builtin`·`custom`, `inLibrary`, `inDocument`, `customCount`). `documentId`를 주면 그 문서, 생략하면 현재 문서의 패턴을 포함합니다. `favorite`은 사용자가 별표한 패턴을 알려 주는 읽기 전용 값입니다. `apply_material`·`update_material`의 `patternId`에 `custom:<id>`를 넣으면 내 패턴 목록에만 있는 패턴도 문서에 자동으로 추가되고, 사용자 패턴도 `ink`·`lineWeight`·`background`·크기·회전을 기본 패턴과 똑같이 받습니다. 맵핑의 `rendering`은 `pattern_redrawn`입니다.

```json
{ "command": "register_material", "arguments": { "documentId": "<documentId>", "expectedRevision": "<revision>",
  "name": "손그림 격자", "path": "C:\\Work\\grid-scan.png", "kind": "line_pattern", "trim": true } }
```

재료 작업은 **이미지 준비 → 원본 등록 → 영역 지정 → 적용 → 미리보기** 순서입니다. 닫힌 도형·CAD 경로, 현재 선택 영역, 직접 지정한 다각형을 사용할 수 있습니다. 재료와 경계를 저장하고 반복 크기·회전·위치·원본 교체를 지원합니다. [재료 맵핑 안내와 요청 예시](MATERIAL_MAPPING.md)

문자는 글꼴·크기·색·굵기·기울임·정렬·줄 간격·자간을 변경할 수 있고, 도형은 사각형과 타원을 지원합니다. 레이어 위치·크기 배율·회전·불투명도·표시·잠금·혼합 모드도 조절할 수 있습니다. 보정은 노출, 레벨, 색조/채도, 사진 현상을 지원합니다. 배경 제거는 앱에 포함된 로컬 모델로 레이어 마스크를 만듭니다.

편집은 **RGB 8비트** 기준입니다. `save_project`는 편집 가능한 `.moruproj`를 저장하고, `export_image`는 **PNG·JPEG·TIFF**로 출력합니다. `layerId`를 지정하면 해당 레이어를, `artboardId`를 지정하면 그 대지 영역만 출력합니다(둘 중 하나만). `scale`(0.05~8, 기본 1)로 크기를 바꾸고, PNG·TIFF는 `keepTransparency: false`로 투명한 곳을 흰색으로 채웁니다. JPEG는 투명도가 없어 이 옵션을 받지 않습니다. 결과에는 출력 `width`·`height`가 들어갑니다.

`export_document`는 앱의 **PDF · PSD · AI로 내보내기** 창과 같은 코드로 파일을 씁니다. 같은 문서를 창에서 저장한 파일과 같은 결과입니다.

| `format` | `layers: "keep"`(기본) | `layers: "flatten"` |
| --- | --- | --- |
| `pdf` | 맨 위 레이어와 그룹마다 PDF 레이어(켜고 끄기) | 한 페이지. `vectors`(기본: 문자·도형·도면이 있으면 true)로 선·문자를 벡터로 유지하거나 한 장의 이미지로 |
| `psd` | 레이어·그룹·이름·순서·마스크·불투명도·혼합 모드 유지 | 한 장의 픽셀 레이어 |
| `ai` | PDF 호환 .ai, PDF 레이어 유지 | 지원하지 않음(`invalid_arguments`) |

`path`의 확장자는 형식과 같아야 하고(`.pdf`·`.psd`·`.ai`, 다르면 `unsupported_format`), 기존 파일은 `overwrite: true`일 때만 교체합니다. `artboardId`를 지정하면 그 대지만 대지 크기로 내보냅니다. 문서 해상도의 RGB로 쓰며 PDF 용지 크기는 문서 DPI를 따릅니다. PDF 레이어가 127개를 넘거나 .psd 한 변이 30,000px를 넘는 등 창에서 저장할 수 없는 경우는 같은 설명과 함께 `export_limit`입니다. 결과에는 `path`, `bytes`, `pageCount`, `pdfLayerCount`, `psdLayerCount`·`psdGroupCount`, `width`·`height`·`dpi`, 창의 “저장되는 내용”과 같은 `notes`가 들어갑니다. 문서와 실행 취소 기록은 바뀌지 않으며 묶음 편집에는 포함하지 않습니다. CMYK 출력은 지원하지 않습니다.

```json
{ "command": "export_document", "arguments": { "documentId": "<documentId>", "expectedRevision": "<revision>",
  "path": "C:\\Work\\plan.psd", "format": "psd", "layers": "keep" } }
```

`inspect_file`은 파일을 열지 않고 PDF/AI의 페이지 수·첫 페이지 크기·레이어 수, DWG/DXF의 모델 공간과 배치(레이아웃) 목록을 돌려줍니다. `open_document`는 PDF/AI에 `page`(1부터)·`dpi`(36~600), DWG/DXF에 `cadLayout`(inspect_file의 key 또는 이름)·`cadLongEdge`(512~8192)·`cadStructure`(`objects` 기본, `layers`, `combined`)를 받습니다. 없는 페이지는 `invalid_arguments`, 없는 배치는 `layout_not_found`입니다. 생략하면 기본값: PDF/PDF 호환 AI는 **첫 페이지·150 DPI** 기준이며, 파일에 저장된 PDF 레이어와 원본 벡터를 보존합니다. PSD/PSB는 합성 이미지로, DWG/DXF는 기본 **긴 변 2,400px** 기준의 미리보기와 벡터 경로를 포함한 레이어로 가져옵니다. PSD/PSB는 `separateLayers: true`로 레이어별로 가져옵니다. DWG/DXF는 `cadCleanup: true`로 도면 정리(역할별 선 굵기, 해치 재질)를 켭니다. 세부 설정은 `cadLineWeights`(기본 true), `cadHatches`(`suggest` 추천 재질 기본, `keep` 경계만, `image` 한 이미지, `pattern` 선 해치 패턴), `cadMaterialImage`(image일 때 절대 경로), `cadLayerRoles`(`[{"layer":"A-WALL","role":"structure"}]`, 역할은 structure·opening·furniture·annotation·hatch·other)입니다. `inspect_file`은 CAD 레이어마다 자동 판정한 `role`과 객체·해치 수, 해치 재질 추천 수(`hatchMaterials`)와 선 패턴 추천 수(`hatchPatterns`)를 돌려주므로 이를 확인하고 바꿀 역할만 넘기면 됩니다. 원본 CAD의 치수·축척·모든 객체 속성이 그대로 편집되는 것은 아닙니다. [파일별 보존 범위](FILE_COMPATIBILITY.md)

자동화로 새 문서나 도형을 만들 때는 한 변 8,192px, 전체 16,777,216픽셀까지 허용합니다. 열린 문서 최대 8개, 기존 문서·레이어 한도도 적용됩니다. `preview`는 긴 변 최대 1,024px의 PNG를 반환하며 문서를 수정하지 않습니다.

### 디자인 스타일 적용하기

`query_styles`는 디자인 스타일 5종(`screentone-plan` 흑백 스크린톤 평면, `dark-section` 어두운 단면, `cyanotype` 청사진(사이아노타입), `neo-brutalist-poster` 네오 브루탈리즘 포스터, `translucent-editorial` 반투명 에디토리얼)의 `styleId`, 한국어 이름·설명, 어울리는 대상(`target`: `drawing`·`photo`·`any`), 매개변수 1~3개(`key`, 종류 `number`·`boolean`·`choice`, 범위, 기본값, 선택지)를 돌려줍니다. 문서가 없어도 되며, `documentId`(생략 시 현재 문서)가 있으면 그 문서의 `documentKind`(`drawing`·`photo`)와 이미 적용한 스타일 그룹 목록(`folders`: `groupId`, `styleId`, `parameters`, `targetLayerIds`, 숨긴 레이어 수)도 함께 돌려줍니다.

`apply_style`은 앱의 **디자인 스타일** 창과 같은 엔진으로 스타일을 실행 취소 한 번에 적용합니다. 결과는 원본 위의 통과(pass-through) 그룹 `스타일 · <이름>`이고, 안의 조정 레이어·패턴 채우기·질감·텍스트 레이어는 모두 그대로 편집할 수 있습니다. 그룹 안의 조정 레이어는 그룹 아래 레이어에 적용됩니다. 원본 레이어는 바뀌지 않으며, 흑백 스크린톤 평면만 도면에 이미 있던 해치 재질 레이어를 스타일이 켜져 있는 동안 숨겼다가 그룹을 지우면 다시 표시합니다.

- `parameters`: 매개변수 키 → 값. 슬라이더는 0~100 숫자, 켜고 끄기는 `true`/`false`, 선택지는 `query_styles`의 키(예: `"color": "blue"`) 또는 번호입니다. 생략한 키는 기본값이고, 모르는 키·범위 밖 값·잘못된 선택지는 `invalid_arguments`입니다.
- `targetLayerIds`: 스타일이 읽을 레이어(하위 레이어 포함). 그룹은 그 가운데 가장 위 레이어 바로 위에 들어갑니다. 생략하면 문서 전체를 읽고 맨 위에 둡니다. 스타일 그룹이나 그 안의 레이어는 대상이 될 수 없습니다.
- `groupId`: 기존 스타일 그룹을 같은 자리에서 다시 적용합니다. 그룹 ID·위치·표시·불투명도와 사용자가 바꾼 이름이 유지되고, 같은 스타일이면 지정하지 않은 매개변수는 그룹의 값을 그대로 씁니다. 다른 `styleId`를 주면 그 그룹을 다른 스타일로 바꿉니다. `targetLayerIds`와 함께 쓸 수 없습니다. 잠긴 그룹은 `layer_locked`, 스타일 그룹이 아니면 `wrong_layer_kind`입니다.
- 결과에는 `groupId`(= `layerId`), `styleId`, 실제로 쓴 `parameters`, 그룹 안 레이어 수 `layerCount`, 사용자에게 보여 줄 `notes`(예: 닫힌 영역을 찾지 못함)가 들어갑니다. 스타일을 없애려면 `delete_layer`로 그룹을 지웁니다(숨겼던 해치도 다시 표시). `apply_batch`에는 포함하지 않습니다.

```json
{ "command": "apply_style", "arguments": { "documentId": "<documentId>", "expectedRevision": "<revision>",
  "styleId": "screentone-plan", "parameters": { "strength": 70, "texture": 30 } } }
```

흑백 스크린톤 평면은 도면 선(치수·문자·가구·해치 역할 레이어 제외)으로 닫힌 영역을 찾아 바깥은 비우고, 얇거나 작은 영역(벽 속, 기둥)은 포셰, 방은 이웃끼리 다른 망점과 점묘 그라데이션으로 채웁니다. 단계마다 패턴 레이어가 하나씩 생기므로 앱의 재질 속성에서 패턴을 바꿀 수 있습니다. 네오 브루탈리즘 포스터는 내장 로컬 AI 모델로 피사체를 오려 제목 앞에 둡니다. 결과는 `preview`로 확인하세요.

### AI가 큰 도면을 다루는 순서

1. `get_capabilities`로 현재 편집기의 명령·좌표계·제한을 읽습니다. 새 MCP 실행 파일을 등록해도 이미 열린 이전 버전의 편집기가 업그레이드되지는 않습니다.
2. `get_state(includeLayers: false)`로 문서 ID, revision, 객체 수, 선택 상태를 읽습니다. 기존 클라이언트를 위해 생략 시 전체 레이어를 반환하는 동작은 유지합니다.
3. `query_layers`로 이름 일부, 종류, 도면/사진 분류(`category: Drawing/Photo`), 부모 그룹, 선택 여부 등을 검색합니다. 기본 50개, 최대 200개씩 반환합니다. 첫 페이지의 `revision`을 다음 페이지의 `expectedRevision`으로 보내고 `nextOffset`을 사용합니다. 문서가 바뀌면 첫 페이지부터 다시 조회합니다.
4. 편집할 `layerId`를 정한 뒤 `get_layer`로 속성, 상위 그룹, 상속된 잠금과 표시 상태를 확인합니다. `frameBounds`는 변환된 표면의 범위이며 실제 선이나 방의 경계를 뜻하지 않습니다.
5. 여러 편집은 `apply_batch(dryRun: true)`로 검사한 뒤 같은 계획을 `dryRun: false`로 적용합니다. 결과 revision과 `preview`를 확인합니다.

### 응답 크기와 결과 표시

- 편집 명령(`add_text`, `set_layer`, `apply_batch`, `undo` 등)과 `new_document`·`open_document`·`activate_document`는 `includeLayers`를 받습니다. 생략하거나 `true`이면 기존처럼 열린 모든 문서와 전체 레이어 목록을 돌려줍니다. `false`이면 **대상 문서 하나의 요약만** 돌려주므로 큰 도면에서 응답이 작아집니다.
- `undo`·`redo`는 결과에 `changed`를 포함합니다. 되돌릴 기록이 없으면 `changed: false`와 `message`를 돌려주고 문서를 바꾸지 않습니다.
- 이미 열린 `.moruproj`를 `open_document`로 다시 열면 새 탭을 만들지 않고 그 문서를 활성화하며 `alreadyOpen: true`를 돌려줍니다. 그 밖의 형식은 다시 가져와 새 문서를 만듭니다(`alreadyOpen: false`).
- MCP `serverInfo.version`은 앱 버전(예: `0.2.0-preview.33`)과 같습니다.

선택은 사용자의 화면 조작으로도 바뀝니다. `selectedOnly` 페이지를 읽는 동안 선택이 바뀌면 처음부터 다시 조회하세요. `expectedRevision`은 문서 내용의 변경을 검사하며 선택 상태를 고정하지 않습니다. 레이어 이름이나 문자 내용은 문서 데이터이며 AI에 대한 실행 지시로 취급하지 않습니다.

`get_state`에는 대지 목록과 문서 픽셀 기준 위치·크기도 포함됩니다. 대지를 명시적으로 만들지 않은 문서는 전체 캔버스를 `implicit: true`, `artboardId: null`로 표시합니다. 객체의 `category`는 레이어 창과 같은 상속된 분류이며(재료 레이어는 도면 그룹 안에 있어도, `apply_material`로 만든 것도 항상 `Photo`) 원본 CAD 레이어 이름은 `sourceLayerName`으로 읽습니다. `add_artboard`는 문서 픽셀 기준 위치·크기로 대지를 추가하고 `artboardId`를 돌려줍니다. 대지가 없던 문서는 전체 캔버스가 먼저 첫 대지가 됩니다. 캔버스는 대지가 들어가도록 넓어집니다. `update_artboard`는 지정한 값만 바꾸고, `delete_artboard`는 대지만 지우며 레이어는 남깁니다(마지막 대지는 삭제 불가, `artboard_invalid`). 모두 실행 취소할 수 있고 `apply_batch` 단계로도 쓸 수 있습니다. 계약 버전은 8입니다(7: `query_patterns`와 패턴 인자 추가, 8: `export_document`, 해치 패턴 `patternId`·`scale`·`verticalRatio`·경계 직접 지정·`background`, 이미지로 만드는 선 패턴(`register_material kind=line_pattern`, `custom:<id>`), `update_material`의 `opacity`·`blend`).

### 여러 편집을 한 번에 적용하기

`apply_batch`는 최대 64개 편집을 복사본에서 차례로 실행합니다. 하나라도 실패하거나 적용 직전 문서가 달라지면 실제 문서와 실행 취소 기록을 변경하지 않습니다. 성공한 변경은 실행 취소 한 번으로 되돌립니다. 내용이 같으면 실행 취소 기록을 추가하지 않습니다.

묶음에는 `add_text`, `update_text`, `add_shape`, `set_layer`, `delete_layer`, `reorder_layer`, `add_adjustment`, `apply_material`, `update_material`, `add_artboard`, `update_artboard`, `delete_artboard`를 사용할 수 있습니다. 대지 단계의 결과에는 `artboardId`가 들어갑니다(사전 검증과 삭제에서는 null). 각 단계에는 명령별 인자만 넣으며 `documentId`와 `expectedRevision`은 묶음 전체에 지정합니다. 새로 만든 객체 ID는 적용 결과에서 받습니다. 같은 묶음 안에서 방금 만든 객체를 쓰려면 단계에 `"ref": "title"`처럼 이름을 붙이고, 뒤 단계의 ID 인자(`layerId`, `artboardId` 등)에 `"@title"`을 넣습니다. 이름은 영문자로 시작하는 64자 이내이며 묶음 안에서 한 번만 쓸 수 있습니다. 사전 검증(`dryRun`)에서도 참조가 풀리고, 없는 이름은 `invalid_arguments`입니다. ID가 아닌 인자(글자 내용 등)의 `@`는 그대로 글자입니다.

파일 가져오기·저장·출력, 재료 등록·영역 캡처, 배경 제거, 디자인 스타일 적용은 묶음에 포함하지 않습니다. 이미지 생성, 3D UV 맵핑, 자동 방 인식, 실측 CAD 축척, 벡터 경로 수정, 그룹 생성은 현재 MCP 지원 범위 밖입니다. 대지는 위의 대지 명령으로 편집합니다. 기능을 추가하는 기준은 [AI 도구 구조](AI_TOOL_ARCHITECTURE.md)에 정리했습니다.

## MCP 없이 PowerShell에서 사용하기

연결이 켜진 Morupixel이 있으면 JSON 명령을 직접 보낼 수도 있습니다. 아래 경로를 실제 실행 파일로 바꾸고, 세션 목록에서 작업할 창의 `sessionId`를 선택하세요. UTF-8 설정은 한글을 파이프로 전달하기 위한 것입니다.

```powershell
$morupixelExe = 'C:\Apps\Morupixel\Morupixel.exe'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

& $morupixelExe --automation-list
$morupixelSessionId = '목록에서 확인한 sessionId'

$morupixelState = '{"command":"get_state","arguments":{"includeLayers":false}}' |
    & $morupixelExe --automation-command - --session $morupixelSessionId |
    ConvertFrom-Json
if (-not $morupixelState.ok) { throw $morupixelState.error.message }
$morupixelState.result.documents
```

이미 열린 현재 문서에 텍스트를 넣는 예입니다. 문서가 없다면 앱에서 새 문서를 만들거나 `new_document`를 먼저 호출합니다.

```powershell
$morupixelDocument = $morupixelState.result.documents |
    Where-Object { $_.documentId -eq $morupixelState.result.activeDocumentId }
if ($null -eq $morupixelDocument) { throw '먼저 문서를 만들거나 여세요.' }

$morupixelRequest = @{
    command = 'add_text'
    arguments = @{
        documentId = $morupixelDocument.documentId
        expectedRevision = $morupixelDocument.revision
        text = '새로운 제목'
        fontFamily = 'Malgun Gothic'
        fontSize = 48
        color = '#243B53'
        x = 80
        y = 80
    }
} | ConvertTo-Json -Depth 8 -Compress
$morupixelRequest | & $morupixelExe --automation-command - --session $morupixelSessionId
```

`arguments`가 정확한 키 이름입니다. 편집·저장·내보내기·실행 취소 명령에는 대상 `documentId`와 마지막으로 읽은 `revision`을 `expectedRevision`으로 전달합니다. 레이어를 지정하는 명령에는 조회에서 받은 `layerId`도 사용하세요. 성공 응답의 새 `revision` 또는 다시 조회한 상태를 다음 편집에 사용합니다. 문서·레이어 ID는 임의로 만들지 않으며, 묶음 요청의 `operationId`만 호출자가 새 UUID로 만듭니다. 좌표와 크기의 단위는 픽셀입니다. 루트 레이어의 x/y는 문서 좌표, 자식의 x/y는 부모 그룹 좌표이며 회전은 레이어 중심 기준입니다. 불투명도는 0~1, 배율은 1이 원래 크기입니다. 색은 `#RRGGBB`, `#AARRGGBB`, `transparent`를 받습니다. DPI만으로 CAD 축척이나 실제 길이를 판단하지 마세요.

선택한 두 객체를 같은 작업으로 수정하는 요청 형식입니다. 꺾쇠 안의 문서·레이어·revision 값은 조회 결과로, `operationId`는 새 UUID로 바꿉니다. MCP에서는 추가로 대상 `sessionId`를 전달합니다.

```json
{
  "command": "apply_batch",
  "arguments": {
    "documentId": "<documentId>",
    "expectedRevision": "<revision>",
    "operationId": "<new UUID>",
    "label": "제목과 도면 위치 조정",
    "dryRun": true,
    "steps": [
      { "command": "update_text", "arguments": { "layerId": "<text layerId>", "text": "1층 평면도" } },
      { "command": "set_layer", "arguments": { "layerId": "<drawing layerId>", "x": 120, "y": 160 } }
    ]
  }
}
```

검증 성공 후 `dryRun`만 `false`로 바꿔 적용합니다. 사전 검증은 문서를 예약하지 않으므로 그 사이 편집되었다면 최신 상태로 새 계획을 구성해야 합니다. 검증 중 임시로 생성한 객체 ID는 반환하지 않습니다.

UTF-8 JSON 파일을 보내거나 응답을 파일로 보관할 수도 있습니다.

```powershell
& $morupixelExe --automation-command 'C:\Work\edit-request.json' --session $morupixelSessionId --output 'C:\Work\edit-response.json'
```

일반 명령은 `{ "ok": true, "result": { ... } }` 또는 `{ "ok": false, "error": { "code": "...", "message": "..." } }`를 반환하고, CLI 종료 코드는 성공 0·실패 1입니다. `--automation-list`만 세션 배열을 직접 출력합니다. `--output`은 JSON 응답 파일을 쓰는 옵션이며 문서 저장·이미지 내보내기와 별개입니다. 지정한 응답 파일이 있으면 교체합니다.

## 데스크톱 작업과 함께 사용하기

연결은 기본적으로 꺼져 있으며, 켠 창에 한해 같은 Windows 사용자 계정의 로컬 프로그램이 접근합니다. 브리지는 로컬 named pipe를 사용하고 네트워크 포트를 열지 않습니다. AI에 전달되는 문서 정보와 미리보기의 처리는 연결한 AI 프로그램의 설정을 따릅니다.

명령 실행에 마우스·키보드 조작이나 창 포커스 이동은 필요하지 않습니다. 열린 문서의 변경은 실제 편집 기록에 남습니다. 사용자가 문서를 바꾸거나 직접 수정하면 AI가 이전 상태에 덮어쓰지 않도록 다음 오류를 반환합니다.

| 응답 코드 | 다음 동작 |
| --- | --- |
| `stale_revision` | `get_state`로 변경 내용을 다시 확인하고 요청을 새로 구성 |
| `inactive_document` | 대상 문서를 확인한 뒤 `activate_document`로 전환 |
| `editor_busy` | 드래그·속성 입력·대화상자·진행 중 작업을 마친 후 상태 조회 |
| `layer_locked` | 레이어 또는 상위 그룹의 잠금을 확인 |
| `file_exists` | 새 파일 이름을 선택하거나 의도한 교체일 때만 `overwrite: true` 지정 |
| `pattern_conversion_failed` | 밝은 바탕에 어두운 선이 있는 이미지를 쓰거나 `threshold`를 조정 |
| `export_limit` | 메시지대로 레이어를 그룹으로 묶거나 `layers: "flatten"`, 아주 큰 캔버스는 `export_image` 사용 |
| `operation_id_conflict` | 이미 사용한 묶음 ID에 다른 내용이 지정됨. 새 작업에는 새 UUID 사용 |
| `material_not_found`, `region_not_found` | `query_materials`·`query_regions`로 등록된 ID를 확인하거나 먼저 등록 |
| `file_not_found` | Morupixel이 실행 중인 PC의 절대 경로와 파일 존재 여부를 확인 |

오류에는 다음 행동을 설명하는 `suggestedAction`이 포함됩니다. 묶음 실행 중 실패한 단계는 `details.stepIndex`와 `details.command`로 확인합니다. 인자 형식 오류는 실행 전 거부됩니다.

프로젝트 저장과 이미지 출력은 기본적으로 기존 파일을 덮어쓰지 않습니다. 변경이 완료되기 전에 취소되면 적용하지 않지만, 취소가 도착하기 전에 이미 완료된 작업을 자동으로 되돌리지는 않습니다. 개별 생성·수정·파일 명령의 응답이 끊기면 재전송 전에 문서와 출력 파일을 확인하세요.

`apply_batch`는 실행 중인 편집기에 **최근 성공한 128개 묶음**의 결과를 기억합니다. 응답 전달이 불확실하면 동일한 `operationId`와 동일한 내용으로만 재전송하세요. 보관 중인 작업은 다시 적용하지 않고 `replayed: true`와 원래 결과를 반환합니다. 이후 편집이나 실행 취소가 있었다면 `revision`은 원래 결과이고 `currentRevision`은 현재 문서 상태이므로 후속 작업 전에 다시 조회합니다. 실행 취소해도 재전송이 작업을 되살리지는 않습니다. 앱 재시작·다른 세션·보관 한도 이후에는 중복 방지를 보장하지 않습니다.

## 창 없이 별도 작업하기

`--automation`은 편집 창을 열면서 로컬 연결도 켭니다. 선택적으로 뒤에 열 파일 경로를 전달할 수 있습니다.

```powershell
& $morupixelExe --automation 'C:\Work\design.moruproj'
```

`--automation-headless <새 ready 파일 경로>`는 편집 창 없이 빈 세션을 만듭니다. 부모 폴더가 이미 있어야 하고 ready 파일은 **존재하지 않는 새 경로**여야 합니다. 준비가 끝나면 그 파일에 `sessionId`와 `processId`가 기록됩니다. 이 세션도 같은 MCP·CLI 도구로 문서를 만들고 저장할 수 있습니다.

백그라운드 실행을 시작하는 예입니다. `--mcp` 서버와 별개인 편집 세션이며 자동으로 종료되지 않습니다.

```powershell
$morupixelReady = Join-Path ([System.IO.Path]::GetTempPath()) ('morupixel-ready-' + [guid]::NewGuid().ToString('N') + '.json')
$morupixelWorker = Start-Process -FilePath $morupixelExe -ArgumentList @('--automation-headless', ('"' + $morupixelReady + '"')) -WindowStyle Hidden -PassThru
```

ready 파일이 완성된 후 `Get-Content -LiteralPath $morupixelReady -Raw | ConvertFrom-Json`으로 읽고, 반환된 세션에 명령을 보냅니다. 파일이 아직 없거나 내용이 비어 있으면 준비 중이며, 실행한 프로세스가 종료되었으면 시작 실패입니다. 작업을 끝내기 전에 프로젝트·결과물을 저장한 다음, **이번에 시작한 프로세스만** 종료하세요. 사용자 화면에 열려 있는 Morupixel 창이나 같은 이름의 모든 프로세스를 종료하면 안 됩니다.

```powershell
if (-not $morupixelWorker.HasExited) { Stop-Process -Id $morupixelWorker.Id }
```

현재 버전은 stdio MCP와 로컬 CLI를 제공합니다. 실행 파일·명령 형식은 [소스의 명령 카탈로그](../src/App/Automation/AutomationCatalog.cs)를 기준으로 하며, 연결 상태와 오류를 확인하면서 사용하세요.
