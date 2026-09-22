# AI 연결

**Preview 22 통합판**에 포함된 AI 연결 안내입니다. 공개 ZIP과 같은 버전의 실행 파일을 사용하세요.

Morupixel을 Codex 같은 외부 AI 도구에 연결하면 문서를 만들고, 이미지·텍스트·도형을 배치하고, 보정한 결과를 저장할 수 있습니다. Morupixel 안에 채팅창이나 언어 모델을 추가하는 기능은 아닙니다. 연결한 AI가 사용자의 요청을 해석하고 Morupixel의 편집 도구를 호출합니다. **Morupixel에는 API 키를 입력하지 않습니다.** AI 서비스의 로그인·모델 설정은 연결하는 프로그램에서 관리합니다.

## 연결해서 사용하기

1. 이 기능이 포함된 Morupixel을 실행하고 **AI 연결 → 로컬 연결 켜기**를 선택합니다.
2. AI 프로그램에 아래 MCP 서버를 등록합니다. **AI 연결 → 연결 설정…**에서도 실행 파일 경로가 들어간 설정을 복사할 수 있습니다.
3. AI에게 “Morupixel의 열린 문서를 확인하고, 현재 문서에 수정 가능한 제목을 넣어줘”처럼 요청합니다.

AI는 `morupixel_list_sessions`로 연결된 창을 찾고, `morupixel_get_state`로 문서와 레이어를 확인한 뒤 작업합니다. 여러 Morupixel 창이 있으면 대상 세션을 구분해야 합니다. 연결을 끝내려면 **AI 연결 → 로컬 연결 켜기**를 다시 선택해 체크를 해제합니다.

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

Codex의 사용자 `~/.codex/config.toml` 또는 신뢰한 프로젝트의 `.codex/config.toml`에 다음 항목을 등록할 수 있습니다. 기존 설정에 병합하고, 이미 같은 서버 이름이 있으면 그 항목을 수정하세요. Windows 경로의 역슬래시는 큰따옴표 TOML 문자열 안에서 `\\`로 적습니다. [OpenAI 공식 MCP 안내](https://developers.openai.com/codex/mcp)

```toml
[mcp_servers.morupixel]
command = "C:\\Apps\\Morupixel\\Morupixel.exe"
args = ["--mcp"]
```

등록 후 사용하는 Codex 클라이언트에서 서버 연결을 새로고침하거나 재시작하세요. 이 설정은 Morupixel이 실행되는 Windows 호스트에 적용합니다. 웹이나 다른 PC의 AI가 이 로컬 실행 파일에 직접 접근하는 설정은 아닙니다.

## 할 수 있는 작업

현재 MCP는 다음 **19개 도구**를 제공합니다. CLI에서는 앞의 `morupixel_`를 뺀 명령 이름을 사용합니다.

| 작업 | MCP 도구 |
| --- | --- |
| 세션·문서 상태 확인 | `morupixel_list_sessions`, `morupixel_get_state` |
| 문서 만들기·열기·전환 | `morupixel_new_document`, `morupixel_open_document`, `morupixel_activate_document` |
| 이미지·수정 가능한 문자·도형 | `morupixel_add_image`, `morupixel_add_text`, `morupixel_update_text`, `morupixel_add_shape` |
| 레이어 속성·삭제·순서 | `morupixel_set_layer`, `morupixel_delete_layer`, `morupixel_reorder_layer` |
| 조정 레이어·AI 배경 제거 | `morupixel_add_adjustment`, `morupixel_remove_background` |
| 프로젝트 저장·이미지 출력·미리보기 | `morupixel_save_project`, `morupixel_export_image`, `morupixel_preview` |
| 실행 취소·다시 실행 | `morupixel_undo`, `morupixel_redo` |

문자는 글꼴·크기·색·굵기·기울임·정렬·줄 간격·자간을 변경할 수 있고, 도형은 사각형과 타원을 지원합니다. 레이어 위치·크기 배율·회전·불투명도·표시·잠금·혼합 모드도 조절할 수 있습니다. 보정은 노출, 레벨, 색조/채도, 사진 현상을 지원합니다. 배경 제거는 앱에 포함된 로컬 모델로 레이어 마스크를 만듭니다.

편집은 **RGB 8비트** 기준입니다. `save_project`는 편집 가능한 `.moruproj`를 저장하고, `export_image`는 **PNG·JPEG·TIFF**로 출력합니다. `layerId`를 지정하면 해당 레이어를 출력합니다. 이 자동화 명령에는 PDF·PSD·CMYK 출력 옵션이 아직 없습니다. 앱 화면에서 제공하는 내보내기 기능의 범위와 구분하세요.

`open_document`는 기존 가져오기 엔진의 기본 설정을 사용합니다. PDF/PDF 호환 AI는 **첫 페이지·150 DPI** 기준이며, 파일에 저장된 PDF 레이어와 원본 벡터를 보존합니다. PSD/PSB는 합성 이미지로, DWG/DXF는 기본 **긴 변 2,400px** 기준의 미리보기와 벡터 경로를 포함한 레이어로 가져옵니다. 페이지·CAD 레이아웃·레이어 분리 같은 세부 가져오기 옵션은 현재 자동화 명령에 없습니다. 원본 CAD의 치수·축척·모든 객체 속성이 그대로 편집되는 것은 아닙니다. [파일별 보존 범위](FILE_COMPATIBILITY.md)

자동화로 새 문서나 도형을 만들 때는 한 변 8,192px, 전체 16,777,216픽셀까지 허용합니다. 열린 문서 최대 8개, 기존 문서·레이어 한도도 적용됩니다. `preview`는 긴 변 최대 1,024px의 PNG를 반환하며 문서를 수정하지 않습니다.

## MCP 없이 PowerShell에서 사용하기

연결이 켜진 Morupixel이 있으면 JSON 명령을 직접 보낼 수도 있습니다. 아래 경로를 실제 실행 파일로 바꾸고, 세션 목록에서 작업할 창의 `sessionId`를 선택하세요. UTF-8 설정은 한글을 파이프로 전달하기 위한 것입니다.

```powershell
$morupixelExe = 'C:\Apps\Morupixel\Morupixel.exe'
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

& $morupixelExe --automation-list
$morupixelSessionId = '목록에서 확인한 sessionId'

$morupixelState = '{"command":"get_state","arguments":{}}' |
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

`arguments`가 정확한 키 이름입니다. 편집·저장·내보내기·실행 취소 명령에는 대상 `documentId`와 마지막으로 읽은 `revision`을 `expectedRevision`으로 전달합니다. 레이어를 지정하는 명령에는 상태에서 받은 `layerId`도 사용하세요. 성공 응답의 새 `revision` 또는 다시 조회한 상태를 다음 편집에 사용하며, ID를 임의로 만들지 않습니다. 좌표와 크기는 문서 픽셀, 불투명도는 0~1, 배율은 1이 원래 크기입니다. 색은 `#RRGGBB`, `#AARRGGBB`, `transparent`를 받습니다.

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

프로젝트 저장과 이미지 출력은 기본적으로 기존 파일을 덮어쓰지 않습니다. 변경이 완료되기 전에 취소되면 적용하지 않지만, 취소가 도착하기 전에 이미 완료된 작업을 자동으로 되돌리지는 않습니다. 응답이 끊겨 결과가 불확실할 때에는 같은 생성·수정 명령을 자동 재전송하지 말고 문서 상태와 출력 파일을 먼저 확인하세요. 중복 실행 방지용 트랜잭션 ID는 현재 제공하지 않습니다.

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
