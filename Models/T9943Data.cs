namespace LS.Futures.Models
{
    /// <summary>
    /// 지수선물 마스터 조회 (t9943) 1행 — 근월물 futcode 확보용(선물 설계 §2.2).
    /// InBlock gubun: "" 전체 (res 실측 2026-07-03).
    /// </summary>
    public class T9943Item
    {
        public string Hname { get; set; }    // 종목명
        public string Shcode { get; set; }   // 단축코드 (101XXXXX)
        public string Expcode { get; set; }  // 확장코드 12자리
    }
}
