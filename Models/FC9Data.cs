using System;

namespace LS.Futures.Models
{
    /// <summary>
    /// KOSPI200 선물 체결 실시간 (FC9, 파생 차세대 9자리 체계 — 구 FC0 대체).
    /// res 실측 필드 기준(2026-07-03 확정, C:\LS_SEC\xingAPI\Res\FC9.res):
    /// 체결 틱에 KOSPI200지수(k200jisu)/이론가/괴리율/시장·이론BASIS가 내장 —
    /// 베이시스 피처를 별도 지수 TR 없이 이 틱에서 직접 계산한다(선물 설계 §3).
    /// RecvTicks = Stopwatch.GetTimestamp() 단조시계 — 감쇠곡선의 레이턴시 축(설계 D6).
    /// </summary>
    public class FC9Data
    {
        public string Futcode { get; set; }      // 단축코드 (101XXXXX)
        public string CheTime { get; set; }      // 체결시간 HHMMSS
        public string Sign { get; set; }         // 전일대비구분
        public decimal Change { get; set; }      // 전일대비
        public decimal Drate { get; set; }       // 등락율
        public decimal Price { get; set; }       // 현재가(체결가)
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public string CGubun { get; set; }       // 체결구분 (+매수/-매도)
        public long CVolume { get; set; }        // 체결량(이번 틱)
        public long Volume { get; set; }         // 누적거래량
        public long Value { get; set; }          // 누적거래대금
        public long MdVolume { get; set; }       // 매도누적체결량
        public long MdCheCnt { get; set; }       // 매도누적체결건수
        public long MsVolume { get; set; }       // 매수누적체결량
        public long MsCheCnt { get; set; }       // 매수누적체결건수
        public decimal CPower { get; set; }      // 체결강도
        public decimal OfferHo1 { get; set; }    // 매도호가1
        public decimal BidHo1 { get; set; }      // 매수호가1
        public long OpenYak { get; set; }        // 미결제약정수량
        public decimal K200Jisu { get; set; }    // KOSPI200지수 (베이시스 계산 원천)
        public decimal TheoryPrice { get; set; } // 이론가
        public decimal Kasis { get; set; }       // 괴리율
        public decimal SBasis { get; set; }      // 시장BASIS
        public decimal IBasis { get; set; }      // 이론BASIS
        public long OpenYakCha { get; set; }     // 미결제약정증감
        public string JGubun { get; set; }       // 장운영정보
        public long JnilVolume { get; set; }     // 전일동시간대거래량

        public DateTime ReceivedAt { get; set; } // 수신 벽시계(KST)
        public long RecvTicks { get; set; }      // 수신 단조시계(Stopwatch.GetTimestamp)
    }
}
