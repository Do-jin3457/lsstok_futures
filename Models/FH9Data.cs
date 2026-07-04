using System;

namespace LS.Futures.Models
{
    /// <summary>
    /// KOSPI200 선물 호가 실시간 (FH9, 파생 차세대 — 구 FH0 대체).
    /// res 실측(2026-07-03): 5단 호가 + 잔량 + 건수(offercnt/bidcnt) + 총잔량/총건수 + 단일가여부.
    /// 인덱스 0 = 1호가 … 4 = 5호가. OFI/queue-imbalance 계산의 원천(선물 설계 §3).
    /// </summary>
    public class FH9Data
    {
        public string Futcode { get; set; }
        public string HoTime { get; set; }        // 호가시간 HHMMSS

        public decimal[] OfferHo { get; set; }    // 매도호가 1~5
        public long[] OfferRem { get; set; }      // 매도호가수량 1~5
        public long[] OfferCnt { get; set; }      // 매도호가건수 1~5
        public decimal[] BidHo { get; set; }      // 매수호가 1~5
        public long[] BidRem { get; set; }        // 매수호가수량 1~5
        public long[] BidCnt { get; set; }        // 매수호가건수 1~5

        public long TotOfferRem { get; set; }     // 매도호가총수량
        public long TotBidRem { get; set; }       // 매수호가총수량
        public long TotOfferCnt { get; set; }     // 매도호가총건수
        public long TotBidCnt { get; set; }       // 매수호가총건수
        public string DanHoChk { get; set; }      // 단일가호가여부

        public DateTime ReceivedAt { get; set; }  // 수신 벽시계(KST)
        public long RecvTicks { get; set; }       // 수신 단조시계(Stopwatch.GetTimestamp)

        public FH9Data()
        {
            OfferHo = new decimal[5];
            OfferRem = new long[5];
            OfferCnt = new long[5];
            BidHo = new decimal[5];
            BidRem = new long[5];
            BidCnt = new long[5];
        }
    }
}
