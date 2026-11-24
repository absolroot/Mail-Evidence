# 검증 자료

이 저장소에는 합성 EML·MBOX와 라이선스를 함께 보존한 Apache POI의 MSG 대조 표본이 포함되어 있습니다. [이상 징후](anomaly-ux-complete/) · [첨부 구조](attachment-boundaries/) · [대화 이력](conversation-history/)

저장소 루트에서 다음 명령을 실행하면 합성 자료로 판독·검색·내보내기와 원본 대조를 시험합니다.

```powershell
dotnet run --project tests/MailViewer.Verification -c Release -- --synthetic-only
dotnet build MailViewer.slnx -c Release
python tools/run-flow-smoke.py artifacts/smoke-attachment-boundaries --scenario attachment-boundaries --theme dark
python tools/run-flow-smoke.py artifacts/smoke-investigation --scenario investigation --theme light
```

형식별 회귀 시험에는 아래 공개 표본을 별도로 준비하세요. 경로는 이 폴더를 기준으로 합니다. 검증 프로그램은 표본의 길이와 해시를 대조합니다.

| 경로 | 공개 출처 |
| --- | --- |
| `external/tika-multipart-attachments.eml` | [Apache Tika EML](https://raw.githubusercontent.com/apache/tika/f1f785f533877ce7426f45b1460ed52e154bcaca/tika-parsers/tika-parsers-standard/tika-parsers-standard-modules/tika-parser-mail-module/src/test/resources/test-documents/testRFC822_multipart_attachments.eml) |
| `external/public-apache-tika.mbox` | [Apache Tika 메일링 리스트 2008년 2월](https://mail-archives.apache.org/mod_mbox/tika-dev/200802.mbox) |
| `external/tika-test-outlook.msg`, `external/poi-attachment-msg-pdf.msg`, `external/tika-testPST.pst`, `pst/libyal-outlook.pst` | [MSG 검증 기록](../../docs/msg-verification.md) · [PST 검증 기록](../../docs/pst-verification.md) |
| `ost/` | 루트에서 `python tests/fixtures/make-ost-oracle.py --fetch-only` 실행 |

형식별 자료를 준비한 뒤 `dotnet run --project tests/MailViewer.Verification -c Release -- --ost --ost-expanded`를 실행합니다.
