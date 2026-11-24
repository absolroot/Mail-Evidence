# 구형 OST와 블록 인코딩 검증

2026년 11월 23일, Mail Viewer 1.4.0 Release. 확장자보다 파일 헤더의 `SO` 표지, NDB 버전, 블록 인코딩 값을 기준으로 표본을 분류했다. 출처가 확인된 파일은 SHA-256을 고정해 로컬 검증에 사용했다. [MS-PST 파일 헤더](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/c9876f5a-664b-46a3-9887-ba63f113abf5) · [libpff PFF 형식 명세](https://github.com/libyal/libpff/blob/main/documentation/Personal%20Folder%20File%20%28PFF%29%20format.asciidoc)

## 표본과 재현 자료

| 검증 자료 | 출처와 실제 헤더 | SHA-256 |
|---|---|---|
| `SampleOstFile.ost` | 공개 예제 원본. `!BDN`, `SO`, Unicode 23, 512바이트 인덱스 페이지, Permute `0x01`, 4,080,640바이트. 이 파일에서 구형 레이아웃과 블록 인코딩을 함께 검증했다. [Aspose 원본](https://github.com/aspose-email/Aspose.Email-for-.NET/blob/88cb2da311382bbbba6d14813127b0e6b3854c12/Examples/Data/MAPI/SampleOstFile.ost) | `5ff8bc133935a03fad8241f271a604e1cc41e36640d6538d6bcc9bd39e6d9866` |
| `generated-aspose-v23-cyclic.ost` | 같은 OST의 외부 데이터 블록 906개를 Cyclic `0x02`로 다시 인코딩한 검증용 사본. 실제 Outlook에서 생성된 Cyclic 원본의 확보 여부와 구분한다. [MS-PST Cyclic 규칙](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-pst/9979fc01-0a3e-496f-900f-a6a867951f23) | `b8153bd133cf20313a8ec59b2674350060186d165ed9d196706890b242700aee` |
| `generated-aspose-v21.ost` | Aspose 원본 사본의 NDB 버전과 관련 헤더 CRC를 변경해 Unicode 21 분기를 확인했다. 독립 판독에서 원본과 동일한 폴더, 메일, 속성을 확인했다. | `9452cb9401e2f3bbd4830599016477cd43e9e30ebdaa8a90579c54bd3595a0cb` |
| `generated-sdk-v14.ost` | 공개 Microsoft PST SDK ANSI 원본의 사본에서 클라이언트 표지를 `SO`로 바꾸고 헤더 CRC를 맞췄다. ANSI 14·512바이트·Permute 판독을 확인했다. [Microsoft PST SDK 표본 사본](https://github.com/emk/pstsdk/blob/21c7d2d57bd09555afd0a08adc966f9b3779cb19/test/sample2.pst) · [표본 출처와 라이선스](https://github.com/bslater/bodu/blob/76abdd27524f59853bd1b994bbf1f2c0e67b62d6/Bodu.IO.Pst/test/Fixtures/Reference/NOTICE.md) | `adf2a442715a56d99a2559c87753a39a3c54b3c925d7f78a4622c19a01e3e10e` |
| `generated-sdk-v15.ost` | 같은 ANSI PST 원본 사본에서 `SO`, NDB 버전 15와 관련 헤더 CRC를 지정해 ANSI 15 분기를 확인했다. 두 ANSI 사본에서 독립 판독 결과 폴더 5개와 메일 1건이 원본과 일치했다. | `9625470a5ed8a8687e45a406f04fbc49017498f419a3bfee8638ec9b59968392` |

Aspose의 `PersonalStorageFile.ost`와 `test.ost`는 `SM` 표지가 있는 PST로 분류했다. ANSI 14·15, Unicode 21, Cyclic `0x02`는 위 재현 자료로 검증했다. 해당 조합의 Outlook 생성 OST는 표본으로 확보하지 못했다. 이전에 검증한 Unicode 36 OST 두 개와 Unicode 23 PST는 이번 회귀 시험에도 사용했다. [Aspose 예제 디렉터리](https://github.com/aspose-email/Aspose.Email-for-.NET/tree/88cb2da311382bbbba6d14813127b0e6b3854c12/Examples/Data/MAPI) · 기존 OST 검증 · [기존 PST 검증](pst-verification.md)

## 독립 판독 기준

별도로 설치한 `libpff-python` 20261030에서 Aspose 원본의 일반 폴더 37개, 지원하는 메일 81건, 전송 헤더 속성 66개와 헤더 행 1,237개를 확인했다. 첨부 35개 중 바이트가 저장된 일반 첨부 22개의 전체 SHA-256과 34개 부분 구간을 대조했다. 나머지 첨부 13개는 일반 파일 바이트를 제공할 수 없는 상태로 표시됐다. [libpff 배포판](https://pypi.org/project/libpff-python/20261030/) · [독립 기준값](../tests/fixtures/ost-expanded-oracle.json)

Mbox Viewer에 포함된 `pst-extractor` 1.12.0으로 원본을 다시 읽었다. 두 판독기에서 같은 일반 폴더 37개와 이 스크립트가 선택한 `IPM.Note` 메일 80건을 확인했다. 공통 메일의 전송 헤더 65개와 일반 첨부 22개의 속성 태그, 길이, SHA-256이 일치했다. `libpff-python` 결과에는 배달 상태 보고 메일 `REPORT.IPM.Note.NotSupportedICal.NDR` 1건이 추가됐다. 두 번째 판독 스크립트

| 대표 근거 | 독립 판독 값 |
|---|---|
| 폴더와 메시지 | `IPM_SUBTREE` NID `0x00002142` → `Inbox` NID `0x00002182` → `newsletter copy` NID `0x00214D84`. `Inbox`의 일반 메일은 11건이다. |
| 전송 헤더 | MAPI `0x007D001F`, 1,872바이트. SHA-256 `3164331448fbd9e9c5e4253911387b1756110252672ddc65c37a74b14e31c3e4`. `Received`는 속성 상대 오프셋 0부터 338바이트다. |
| 첨부 | `image001.jpg`, 첨부 NID `0x000082A5`, MAPI `0x37010102`, 20,833바이트. SHA-256 `0f4ff85802be13ad86f82b7e6e886e5a9a6a77ddb84995bc5a7657a3d40c8f9a`. |

## 통합 콘솔 결과

Release 빌드에서 경고 0개, 오류 0개를 확인했다. `--ost --ost-expanded --large`로 실행한 26건이 모두 통과했다. 새 시험 3건에는 다음 동작을 포함했다. [확장 OST 시험 코드](../tests/MailViewer.Verification/ExpandedOstChecks.cs)

- 구형 원본과 각 검증용 사본의 폴더 이름, 상위 NID, 메일 NID와 제목, 저장된 헤더와 첨부 속성의 전체 바이트를 독립 판독 기준과 대조했다. 헤더가 누락된 메일에는 원본 바이트와 속성 위치가 생성되지 않았다.
- `Inbox`에서 `image001.jpg`를 검색하면 현재 원본의 메일 1건을 찾았다. 상위 폴더의 직접 범위에서는 0건, 하위 포함에서는 받은 편지함과 보낸 편지함의 2건을 찾았다. 실제 Permute 원본과 Cyclic 사본의 NID가 같아도 자료 ID로 검색 결과를 구분했다.
- 실제 Permute 원본과 Cyclic 사본의 전송 헤더와 첨부를 내보낸 뒤 독립 SHA-256과 대조했다. 매니페스트에서 원본 SHA-256, NDB 버전, 페이지 크기, 블록 인코딩과 확정한 폴더 조건을 확인했다. 저장한 첨부에서 길이를 유지한 채 마지막 바이트를 바꾸자 검증에 실패했다. 같은 검색을 실행했을 때 원래 메일이 다시 선택 대상에 포함됐다.
- 512바이트 NBT 루트 손상과 미지원 WIP 인코딩을 구분해 표시했다. 같은 세션의 정상 PST와 두 OST는 계속 조회됐고, 시험 전후 원본 SHA-256도 일치했다.

실제 UI에서 폴더 탐색부터 내보내기 검증과 검색 복귀까지 실행한 결과는 별도 검수 기록에 있다. [확장 OST 화면 검수](ost-expanded-ui-verification.md)

표본 없이 실행한 임시 작업공간에서 기본 시험은 확장 OST 항목을 건너뛰었다. `--ost-expanded`를 명시하면 획득 명령과 함께 실패했다. 표본을 다시 확보하고 두 종류 OST 회귀를 실행하려면 아래 명령을 사용한다. 공개 OST와 파생 바이너리는 로컬 표본 디렉터리에만 둔다. [표본 획득](../tests/fixtures/make-ost-oracle.py) · [검증용 사본 생성](../tests/fixtures/encode-ost-control.py)

```powershell
python Mail-Viewer/tests/fixtures/make-ost-oracle.py --fetch-only
python Mail-Viewer/tests/fixtures/make-ost-oracle.py --expanded --fetch-only
dotnet build Mail-Viewer/MailViewer.slnx -c Release -warnaserror
dotnet run --project Mail-Viewer/tests/MailViewer.Verification -c Release --no-build -- --ost --ost-expanded --large
```
