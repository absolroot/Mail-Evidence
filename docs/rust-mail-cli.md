# Rust 메일 CLI 0.4.0

Windows x64의 명령줄에서 메일 목록과 첨부를 내보냅니다. 파일과 폴더를 한 번에 지정할 수 있으며, 폴더를 지정하면 하위 폴더도 검색합니다. [CLI 다운로드](https://github.com/absolroot/Mail-Evidence/releases/download/v1.5.7/Mail-Evidence-CLI-v0.4.0-win-x64.zip)

```powershell
./mailviewer-engine.exe report D:/Evidence/Mail
./mailviewer-engine.exe report --csv D:/Evidence/Mail | Set-Content -Encoding utf8 D:/Cases/messages.csv
$result = ./mailviewer-engine.exe extract D:/Cases/Output D:/Evidence/Mail | ConvertFrom-Json
./mailviewer-engine.exe verify $result.directory
```

| 명령 | 결과 |
| --- | --- |
| `report` | 원본 경로·SHA-256, 메일 정보, 본문, 헤더와 첨부 위치를 JSON으로 출력합니다. 누락된 값과 판독 오류도 기록합니다. |
| `report --csv` | 메일 목록을 UTF-8 CSV로 출력합니다. 수식으로 해석될 수 있는 값에는 `[text] `를 붙입니다. |
| `extract` | 매번 새 결과 폴더에 `messages.csv`, `manifest.json`, `attachments/`를 만듭니다. 첨부의 길이와 해시를 대조하고, 이름이 같아도 별도 파일로 저장합니다. |
| `verify` | 매니페스트에 기록된 길이와 SHA-256을 검사합니다. 파일이 누락되거나 달라지면 실패로 종료합니다. |

## 로컬 메일함

| 자료 | 입력 |
| --- | --- |
| Maildir · MH | Maildir는 `cur/`와 `new/`, MH는 숫자 이름의 메시지와 `.mh_sequences`가 있는 폴더를 지정합니다. |
| Apple Mail | `Messages/`와 `Attachments/<메시지 번호>/<MIME 파트 번호>/`의 상대 경로를 유지합니다. |
| Thunderbird | 메일 폴더를 직접 지정하거나 `inspect-profile thunderbird`로 프로필을 지정합니다. `Mail/`, `ImapMail/`과 `.sbd` 계층을 읽고 `.msf`는 보조 색인으로 식별합니다. |
| Windows Mail · Windows Live Mail | 저장 폴더를 직접 지정하거나 `inspect-profile windows-mail`로 EML을 찾습니다. |
| Outlook Express | 저장 폴더나 `inspect-profile outlook-express`로 찾습니다. 폴더·색인 파일도 지정하면 메시지와 저장소의 관계가 JSON에 기록됩니다. |

## 입력과 첨부

| 입력 | 추출되는 바이트 |
| --- | --- |
| EML·MBOX·EMLX·Maildir·MH·MMDF·Babyl | 전송 인코딩을 해제한 MIME 첨부. 부분 EMLX는 위치를 찾은 외부 첨부도 포함합니다. |
| MSG | CFB에 저장된 일반 첨부 스트림. 내장 메시지의 일반 첨부도 추출할 수 있습니다. |
| PST·OST | 로컬에서 읽을 수 있는 일반 첨부의 MAPI 데이터 속성. |
| Outlook Express DBX·MBX | 메일 원본에서 읽은 MIME 첨부. |
| OLM·ZIP | OLM의 첨부 파일과 ZIP 안의 EML·EMLX·MBOX에서 읽은 MIME 첨부. |
| 독립 TNEF | 일반 첨부의 데이터 속성. |

첨부를 읽을 수 없으면 `unavailable`, 기록한 길이·해시와 달라졌거나 저장 단계에서 읽지 못하면 `failed`로 표시합니다. 서버 참조 첨부, 보호된 내용의 복호화, OLE 개체, 내장 Outlook 항목의 독립 MSG 재구성, EDB·NSF 판독은 지원 범위 밖입니다. 파일 범위나 MSG 스트림을 직접 저장하는 명령은 `mailviewer-engine.exe --help`에서 확인할 수 있습니다.

## 검증

공개 자료와 합성 자료의 메일 75건에서 첨부 96개를 추출했습니다. EML은 Python MIME, MSG는 olefile, PST·OST는 독립 판독 기준값, OLM은 ZIP 원본 멤버와 대조했습니다. 첨부를 변경하거나 삭제했을 때 `verify`가 이를 검출했습니다. [검증 표본](../tests/fixtures/README.md)
