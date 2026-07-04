using System.IO;

namespace LS.Futures.Shared
{
    /// <summary>
    /// res 파일 경로 헬퍼 — LS.Core AppSettings 의존 제거용 자체 구현(완전 독립 방침).
    /// ⚠️ ConfigureResPath 미호출 시 GetResPath가 null 조합으로 전 TR 死(6/29 사고 계보) — 부팅 첫 단계에서 주입.
    /// </summary>
    public static class ResPath
    {
        private static string _dir = "";

        public static void Configure(string resDir) { _dir = resDir ?? ""; }

        public static string Get(string resFileName)
        {
            return string.IsNullOrEmpty(_dir) ? resFileName : Path.Combine(_dir, resFileName);
        }
    }
}
