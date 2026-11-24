# 검증용 메일

| 샘플 | 확인할 내용 |
| --- | --- |
| [이상 징후](anomaly-ux-complete/) | 헤더, 링크, 첨부에서 발생하는 징후와 확인 불가 상태 |
| [첨부 구조](attachment-boundaries/) | 시작과 종료 시그니처, 구조 길이, 종료 뒤 추가 바이트 |
| [대화 이력](conversation-history/) | 회신 주소 변경과 관련 메일 |
| [일반 메일](everyday/) | 주문 영수증, 예약 확인, 이름이 같은 첨부, 긴 제목 |

## 합성 자료로 실행

저장소 루트에서 실행합니다. 화면 시험은 [Windows 화면 검증](../../docs/ui-verification.md)을 참고하세요.

```powershell
dotnet run --project tests/MailViewer.Verification -c Release -- --synthetic-only
```

## 공개 원본으로 실행

아래 파일을 이 폴더를 기준으로 표시한 경로에 저장합니다. 검증 프로그램은 파일의 SHA-256을 대조합니다. Apache POI의 소형 MSG 표본과 라이선스는 [poi-msg](poi-msg/)에 포함돼 있습니다.

| 저장할 경로 | 원본 |
| --- | --- |
| `external/tika-multipart-attachments.eml` | [다운로드](https://raw.githubusercontent.com/apache/tika/f1f785f533877ce7426f45b1460ed52e154bcaca/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-mail-module/src/test/resources/test-documents/testRFC822_multipart_attachments.eml) |
| `external/public-apache-tika.mbox` | [다운로드](https://mail-archives.apache.org/mod_mbox/tika-dev/200802.mbox) |
| `external/tika-test-outlook.msg` | [다운로드](https://raw.githubusercontent.com/apache/tika/f1f785f533877ce7426f45b1460ed52e154bcaca/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-microsoft-module/src/test/resources/test-documents/test-outlook.msg) |
| `external/poi-attachment-msg-pdf.msg` | [다운로드](https://raw.githubusercontent.com/apache/poi/c94ee8ad9096bdb8cf8e5e59d8d12432e950dbf3/test-data/hsmf/attachment_msg_pdf.msg) |
| `external/tika-testPST.pst` | [다운로드](https://svn.apache.org/repos/asf/tika/branches/2.x/tika-test-resources/src/test/resources/test-documents/testPST.pst?p=1940239) |
| `pst/libyal-outlook.pst` | [다운로드](https://raw.githubusercontent.com/libyal/testdata/c3631ff17ce4ae6798ed9abd065c6a67ad626bce/pst/outlook.pst) |

OST 시험은 아래 명령으로 원본과 검증용 사본을 준비합니다. 4 GiB 이후의 MBOX 오프셋도 시험하려면 콘솔 명령에 `--large`를 붙입니다.

```powershell
python tests/fixtures/make-ost-oracle.py --fetch-only
python tests/fixtures/make-ost-oracle.py --expanded --fetch-only
dotnet run --project tests/MailViewer.Verification -c Release -- --ost --ost-expanded
```

[EML·MBOX 검증](../../docs/verification.md) · [MSG 검증](../../docs/msg-verification.md) · [PST 검증](../../docs/pst-verification.md) · [OST 검증](../../docs/ost-expanded-verification.md)
