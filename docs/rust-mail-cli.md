# Rust 메일 CLI 0.4.0

Windows x64에서 로컬 메일 파일과 메일함을 읽는다. 여러 파일이나 디렉터리를 한 번에 지정할 수 있고, 디렉터리는 하위까지 탐색한다. [실행 파일](https://github.com/absolroot/Mail-Evidence/releases/download/v1.5.6/Mail-Evidence-CLI-v0.4.0-win-x64.zip)

```powershell
./mailviewer-engine.exe report D:/Evidence/Mail
./mailviewer-engine.exe report --csv D:/Evidence/Mail | Set-Content -Encoding utf8 D:/Cases/messages.csv
$result = ./mailviewer-engine.exe extract D:/Cases/Output D:/Evidence/Mail | ConvertFrom-Json
$result.counts
./mailviewer-engine.exe verify $result.directory
```

| 명령 | 결과 |
|---|---|
| `report` | JSON으로 원본 경로·SHA-256, 제목, 발신·수신, 원문 날짜, Message-ID, 폴더, 본문, 헤더와 첨부의 원본 위치·해시를 보여 준다. 원본에 없는 값은 빈 값으로 남기며, 판독 오류와 첨부 상태를 함께 기록한다. |
| `report --csv` | 같은 범위의 메일 목록을 UTF-8 CSV로 출력한다. 스프레드시트에서 수식으로 실행될 수 있는 셀에는 `[text] `를 붙인다. JSON에는 판독한 값을 그대로 둔다. |
| `extract` | 선택한 출력 경로 아래에 새 결과 폴더를 만들고, `messages.csv`, `manifest.json`, `attachments/`를 저장한다. 첨부를 저장하기 전에 판독한 길이·SHA-256과 대조한다. 이름이 같은 첨부도 별도 파일로 저장한다. |
| `verify` | 매니페스트에 저장 성공으로 기록된 첨부 파일의 길이와 SHA-256을 확인한다. 변경되거나 누락된 파일의 이름을 출력하고 실패 종료 코드를 반환한다. |

## 입력과 첨부

| 입력 | 추출하는 첨부 |
|---|---|
| EML·MBOX·EMLX·Maildir·MH·MMDF·Babyl | MIME 전송 인코딩을 해제한 첨부 바이트. 부분 EMLX는 확인된 외부 첨부 원본 바이트. |
| MSG | CFB에 저장된 일반 첨부 스트림. 내장 MSG는 하위 메일로 정리하며 그 안의 일반 첨부도 추출할 수 있다. |
| PST·OST | 로컬 저장소에서 읽을 수 있는 파일 첨부의 MAPI 데이터 속성. |
| Outlook Express DBX·MBX | 메시지 원본 구간에서 읽은 MIME 첨부. 폴더 인덱스와 연결 정보는 JSON에 기록한다. |
| OLM·지원 메일이 들어 있는 ZIP | OLM의 첨부 멤버와 ZIP 내부 EML·EMLX·MBOX의 MIME 첨부. |
| 독립 TNEF | 검증 가능한 일반 첨부 데이터 속성. |

첨부 바이트를 확인할 수 없으면 매니페스트에 `unavailable`로 남긴다. 원본에서 읽은 바이트가 추출 단계에서 달라지거나 다시 읽을 수 없으면 `failed`로 남긴다. 서버 참조 첨부나 보호된 내용의 원문, OLE 개체, 내장 Outlook 항목의 독립 MSG 재구성, EDB·NSF 판독은 지원 범위 밖이다. 원본 헤더의 해석 결과와 위치는 JSON에 들어가며, 헤더 스트림 자체가 필요할 때는 형식별 원본 명령을 사용한다. 원본 구간과 스트림 명령

## 검증

Windows x64 실행 파일의 SHA-256은 `6c8aa4561ccd7ab677dc06c5f16086abecee84e712934c9a07f6858b66f8e24f`이다.

공개 표본과 형식별 대조 자료에서 메일 75건을 정리하고 첨부 96개를 추출했다. 공개 EML은 Python MIME, MSG는 olefile, PST·OST는 독립 판독기의 기준 해시, OLM은 ZIP 원본 멤버와 대조했다. 실제 Outlook Express DBX·MBX, 공개 부분 EMLX, 독립 TNEF의 추출값도 대조했다. MMDF·Babyl·MH와 ZIP의 메일은 검증용으로 생성했다. 결과 파일을 변경하거나 삭제한 경우 `verify`가 각각을 검출했고, 재실행은 새 결과 폴더를 만들었다. [검증 자료](../tests/fixtures/README.md)
