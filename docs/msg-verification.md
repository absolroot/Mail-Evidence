# MSG 판독과 내보내기 검증

2026년 11월 23일. Release 빌드에서 **15건 중 15건**을 통과했다. MSG 시험 5건을 추가했고, 기존 EML·MBOX 시험 10건은 4 GiB 경계 시험까지 함께 실행했다. 콘솔은 화면과 같은 엔진과 내보내기 서비스를 호출한다.

```powershell
dotnet build tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release -warnaserror
dotnet run --project tests/MailViewer.Verification/MailViewer.Verification.csproj -c Release --no-build -- --large
```

## 독립 기준값

Python `olefile` 0.47로 CFB 스트림을 직접 열어 전체 바이트와 SHA-256을 기록했다. 전송 헤더의 필드 순서와 시작 위치는 그 스트림에서 구했다. 콘솔은 해당 바이트를 제품에서 읽은 스트림, 화면에서 사용하는 필드별 범위, 저장 파일과 대조한다. [전체 바이트와 해시](../tests/fixtures/msg-oracle.json) · [검증 코드](../tests/MailViewer.Verification/MsgChecks.cs)

| 공개 표본 | 원본 근거와 결과 |
|---|---|
| Apache Tika Outlook Express MSG | 루트 스트림 `/__substg1.0_007D001E`는 377바이트이고 SHA-256은 `ac174e5029430171be1729c355ee41071facfc5369ccfaaaa1d9a888ab5d9af6`이다. 순서대로 여덟 필드를 읽었다. `Date`의 원문 `Thu, 5 Apr 2007 09:26:06 -0700`과 해석값 16:26:06 UTC, `Content-Type`의 스트림 위치 222와 `text/html`, `X-MimeOLE`를 확인했다. 제목 `Express 6`과 본문 `Synchronisation hors connexion`이 각각 한 건에 적중했다. [표본 경로와 다운로드](../tests/fixtures/README.md) |
| Apache POI PDF·중첩 MSG | Unicode 헤더 스트림 `/__substg1.0_007D001F`는 1,574바이트이고 SHA-256은 `faea65c3fae66107d7d1fb63b26edb5b28c3c26619230f1a8012241f1f681d4c`이다. 열일곱 필드의 순서와 위치를 확인했다. 제목 `test email` 및 첨부 이름 `KdcPjl.pdf` 검색이 같은 레코드에 적중했다. PDF 스트림은 `/__attach_version1.0_#00000001/__substg1.0_37010102`, 전체 13,539바이트, SHA-256 `1bd629440fff7a30e340c95e51f2732f239ff7115be211aaa23ba498d0f1b208`이다. 전체와 논리 오프셋 4,090부터 43바이트를 각각 대조했다. 중첩 객체는 `/__attach_version1.0_#00000000/__substg1.0_3701000D` 저장소로 표시됐다. [고정 커밋 원본](https://github.com/apache/poi/blob/c94ee8ad9096bdb8cf8e5e59d8d12432e950dbf3/test-data/hsmf/attachment_msg_pdf.msg) |
| Apache POI 중복 헤더·헤더 없음·HTML 전용 MSG | `simple_test_msg.msg`에서 같은 이름의 `Received` 두 줄을 논리 오프셋 134와 380에서 원래 순서대로 읽고 접힌 줄의 원본 바이트를 확인했다. `blank.msg`에서는 전송 헤더 속성 부재 상태를 확인했다. `HTMLBodyBinary_UTF-8.msg`에서는 HTML의 표시 텍스트 `HTML öäü`를 찾아 검색 결과와 대조했다. [고정 커밋 표본과 고지](../tests/fixtures/poi-msg/) |

## 내보내기와 장애 처리

- **MSG 원본 사본**은 두 대표 입력을 각각 `.msg`로 저장했다. 전체 바이트와 SHA-256이 원본과 일치했다. 매니페스트의 각 레코드에서 헤더 상태, CFB 스트림 이름, MAPI 태그와 길이를 확인했다.
- **PDF 첨부 저장**은 CFB의 by-value 스트림을 파일로 저장했다. 저장 파일이 독립 오라클과 전 바이트 일치했고, 매니페스트의 첨부 근거에 저장소 경로·스트림 이름·MAPI 태그가 남았다. 해당 근거는 CFB 스트림의 논리 위치다.
- **검색 결과 CSV**는 제목으로 확정한 한 건의 레코드 ID와 제목을 기록했다. 원본 MSG·PDF·CSV 패키지를 다시 검증해 파일 크기와 SHA-256이 일치하는지 확인했다. 저장 전후 원본 MSG 두 파일의 SHA-256이 같았다.
- **손상 입력**은 대표 원본의 처음 512바이트만 담은 시험 복사본으로 재현했다. 같은 작업에서 연 정상 MSG는 검색할 수 있었고 손상 자료에는 판독 사유가 남았다. Windows에서 조사 세션이 보유한 원본 파일에 쓰기 핸들을 열면 거부됐다.

## 확인 범위

MSG 안의 중첩 객체는 저장소 위치를 확인했다. 중첩 객체를 독립 MSG로 재직렬화하는 기능은 지원하지 않는다. 같은 고정 커밋의 일반 POI MSG 9개를 추가로 검사했으며, 확인한 압축 RTF는 모두 평문 본문과 함께 저장되어 있었다. 이 시험은 실제 RTF 전용 메시지의 복원 정확도, 모든 MAPI 코드 페이지, 헤더가 비어 있거나 개별 헤더 스트림만 손상된 경우를 판정하지 않는다. MSG의 파일·저장소 구조와 전송 헤더 타입은 Microsoft 공개 규격을 기준으로 삼았다. [MS-OXMSG](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxmsg/) · [전송 헤더 속성](https://learn.microsoft.com/en-us/office/client-developer/outlook/mapi/pidtagtransportmessageheaders-canonical-property) · [EML·MBOX 기반 검증](verification.md)
