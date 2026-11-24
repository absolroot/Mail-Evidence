# Windows 이메일 포렌식 뷰어 검증 기록

2026년 11월 23일, Windows 11 Pro 빌드 26200의 표준 무결성 수준에서 .NET SDK 10.0.102를 사용했다. 콘솔 검증은 화면과 같은 엔진·내보내기 서비스를 호출한다. 검증 코드가 별도로 원본과 저장 파일을 읽고 SHA-256, 바이트 범위, 순서 및 CSV 행을 비교한다.

## 실행

```powershell
dotnet build tests/MailViewer.Verification/MailViewer.Verification.csproj --nologo
dotnet run --project tests/MailViewer.Verification/MailViewer.Verification.csproj -- --large
```

빌드는 경고와 오류가 각각 0건이었다. 콘솔 시험은 **10건 중 10건**을 통과했다. `--large`를 생략하면 Windows에서 스파스 파일로 진행하는 4 GiB 경계 시험을 건너뛴다. [검증 코드](../tests/MailViewer.Verification/Program.cs) · [4 GiB 경계 시험](../tests/MailViewer.Verification/LargeMbox.cs)

## 입력과 독립 기준값

| 입력 | 확보 및 기준값 | 검증 결과 |
|---|---|---|
| 합성 EML | 1,148바이트. SHA-256 `9a8106fd358cd0be272e515248196d4fd6cb31a21baae41607bc5360059c61ad`. | 접힌 `Received`의 원본 바이트와 두 줄의 순서가 일치했다. 날짜의 `+0900`이 유지됐으며 UTC는 03:00이었다. Plain 및 HTML 전용 검색어는 각각 같은 메일의 해당 MIME 파트에서 적중했다. 8바이트 첨부 해시도 일치했다. |
| 합성 MBOX | SHA-256 `390e94e0e8755c5230eadc30f488550d99700ddc70296f27a418f9a7ade36b95`. 범위 기준값 | 메시지의 오프셋과 길이는 각각 `50/1149`, `1249/304`, `1603/315`바이트였다. 메시지 바이트와 SHA-256이 일치했고 구분 `CRLF` 2바이트는 별도 범위로 남았다. 같은 Message-ID 두 건이 유지됐다. 마지막 메시지의 봉투 시각은 03:03, 메시지 Date는 03:02였으며 날짜 범위에는 둘 다 메시지 Date를 기준으로 포함됐다. |
| Apache Tika 공개 EML 시험 자료 | 1,904바이트. SHA-256 `86e5feb18e16178359d6aaa704bd74ceebe7a41f302df5b656f5e6fe0ea34cc5`. [고정 커밋의 공개 원본](https://github.com/apache/tika/blob/f1f785f533877ce7426f45b1460ed52e154bcaca/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-mail-module/src/test/resources/test-documents/testRFC822_multipart_attachments.eml) | `Test TxtA.txt`와 `Test TxtB.txt`를 이름으로 각각 찾았다. Python 표준 MIME 판독기가 디코딩한 첨부와 바이트 길이 33 및 각 SHA-256이 일치했다. 저장한 첨부 A의 SHA-256은 `710b0ea810c12c93cae9982b60ca333e5302e72b8948b194f611513d4737c97d`였다. |
| Apache Tika 개발 메일 공개 MBOX | 2008년 2월 아카이브. 139,161바이트. SHA-256 `76f15052d9cd1a3c8157d945f2a3778f469f36d550bffd27c986bd6f0df8bfe7`. Python 표준 `mailbox` 및 봉투 줄의 원시 바이트 기준으로 36건이다. [Apache 다운로드](https://mail-archives.apache.org/mod_mbox/tika-dev/200802.mbox) | 36건을 읽었고 제목 `tika`는 19건, `mime`은 4건이었다. `mime`의 원본 시작 위치는 32,630, 35,955, 39,859, 44,219였다. 첫 결과는 원본 헤더 2,059바이트와 `Received` 8줄을 확인했다. CSV와 매니페스트에서 고정한 4건의 순서와 범위를 확인했다. |

## 산출물과 장애 처리

- 독립 EML과 MBOX 원본 구간을 따로 저장하고 각 SHA-256과 원본 위치를 매니페스트와 대조했다. 결과를 같은 길이로 변조하거나 삭제하자 검증이 실패했다. 원본 두 파일의 SHA-256은 저장 전후 동일했다.
- 검색 결과에 포함돼야 할 행을 빼고 내보내기를 요청하자 서비스가 범위 변경을 거부했다. 올바른 검색 결과는 지정 순서대로 CSV에 저장됐다. 쉼표와 한글이 있는 수식 형태의 제목은 CSV에서 `[text] `로 시작했고 엔진은 원래 제목을 유지했다.
- 취소한 열기와 내보내기가 취소를 반환했고 취소한 내보내기는 결과 폴더를 생성하지 않았다. 같은 범위를 다시 저장해도 이전 결과가 유지됐다. Windows 원본 핸들이 열려 있을 때 같은 파일에 쓰려는 동작이 거부됐고, 핸들을 닫은 뒤 크기와 수정 시각이 같은 다른 내용으로 교체해 다시 열자 새 검색 결과와 해시를 반환했다.
- 스파스 MBOX의 전체 크기는 4,294,968,626바이트였다. 첫 번째 과대 메시지는 원인을 기록해 건너뛰었고 4,294,967,475번 바이트에서 시작하는 다음 메시지의 검색, 1,149바이트 원본 대조, 저장과 해시 검증이 성공했다. 검색 결과 매니페스트는 건너뛴 구간을 기록했다. 이 시험은 64비트 파일 위치를 확인하며 일반 대용량 자료의 처리량은 별도 측정이 필요하다.

콘솔 검증은 출시 시험 설계의 파싱·근거·내보내기 일부를 재현한다. 프로세스 강제 종료 주입, 모든 MBOX 변형·손상 조합, 통제된 네트워크 캡처와 정식 성능 비교는 이 기록의 검증 범위 밖이다.
