# Windows 화면 검증

저장소 루트에서 Release 빌드를 만든 뒤 실행합니다. 각 명령에는 아직 없는 출력 폴더를 지정하세요. 테스트는 별도 Windows 데스크톱에서 실행하며 결과 JSON과 스크린샷을 그 폴더에 저장합니다.

```powershell
dotnet build MailViewer.slnx -c Release
python tools/run-flow-smoke.py artifacts/smoke-attachment-boundaries --scenario attachment-boundaries --theme dark
python tools/run-flow-smoke.py artifacts/smoke-investigation --scenario investigation --theme light
python tools/run-flow-smoke.py artifacts/smoke-languages --scenario languages
```

EML·MBOX·MSG·PST·OST 원본으로 시험하려면 [형식별 표본](../tests/fixtures/README.md)을 준비하고 `--scenario mail`, `msg`, `pst`, `ost`, `ost-expanded` 중 하나를 지정합니다.

## 형식별 대조 지점

아래는 2026년 11월 23일 Windows 검증에서 확인한 대표 값입니다. 오프셋의 기준은 형식마다 다릅니다.

| 형식 | 화면에서 대조한 값 |
| --- | --- |
| EML·MBOX | 공개 EML의 `Test TxtA.txt` MIME 범위는 파일 오프셋 `0x575`에서 45바이트입니다. 저장한 첨부는 디코딩 후 33바이트였습니다. 공개 Apache MBOX에서 제목 `mime` 검색 결과 네 건의 순서는 CSV와 일치했습니다. [EML·MBOX 기준값](verification.md) |
| MSG | ANSI `0x007D001E`와 Unicode `0x007D001F`의 전송 헤더를 각각 CFB 스트림과 대조했습니다. POI 표본의 PDF 첨부는 스트림 오프셋 0에서 `%PDF-`로 시작하며 13,539바이트입니다. [MSG 기준값](msg-verification.md) |
| PST | Tika 메시지 NID `0x00200024`의 전송 헤더 속성은 10,660바이트입니다. libpff 표본의 `Inbox`에서 찾은 `1.jpg`는 첨부 NID `0x00008025`, 속성 `0x37010102`, 1,969바이트였습니다. [PST 기준값](pst-verification.md) |
| OST | Aspose 표본은 일반 메일 81건을 읽었습니다. `newsletter copy`의 `Received`는 디코딩한 전송 헤더 속성의 처음 338바이트입니다. `image001.jpg`의 첨부 NID는 `0x000082A5`이고 길이는 20,833바이트였습니다. [OST 기준값](ost-expanded-verification.md) |

각 형식에서 내보내기 전후의 원본 SHA-256을 대조했습니다. 화면에서 저장 결과를 검증하고 돌아왔을 때 검색어와 선택한 메일이 유지되는지 확인했습니다. WPF의 논리 포커스와 키 이벤트를 시험했습니다.
