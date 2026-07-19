using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LS.Futures.Models;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Microsoft.Owin.FileSystems;
using Microsoft.Owin.StaticFiles;
using Newtonsoft.Json;
using Owin;

namespace LS.Futures
{
    /// <summary>엔진 컴포넌트 ↔ 웹 상태 허브 (v5: 다중 종목 파이프라인).</summary>
    public static class FuturesApp
    {
        public static FuturesRecorder Recorder;

        // 종목 파이프라인 (등록 순서 유지 — 대시보드 탭 순서). STA에서 등록, 웹은 읽기 전용.
        private static readonly List<InstrumentPipeline> _pipelines = new List<InstrumentPipeline>();
        private static readonly Dictionary<string, InstrumentPipeline> _byCode = new Dictionary<string, InstrumentPipeline>();

        private static volatile bool _loggedIn;
        private static volatile string _loginCode = "";
        private static List<T9943Item> _miniMonths = new List<T9943Item>();
        private static readonly ConcurrentQueue<string> _logs = new ConcurrentQueue<string>();
        private static readonly DateTime _bootAt = DateTime.Now;

        public static void SetSession(bool ok, string code) { _loggedIn = ok; _loginCode = code ?? ""; }
        public static void SetMiniMonths(List<T9943Item> items) { if (items != null) _miniMonths = items; }

        public static void AddPipeline(InstrumentPipeline p)
        {
            lock (_pipelines) { _pipelines.Add(p); _byCode[p.Cfg.Code] = p; }
        }

        public static InstrumentPipeline Find(string code)
        {
            lock (_pipelines)
            {
                InstrumentPipeline p;
                if (!string.IsNullOrEmpty(code) && _byCode.TryGetValue(code, out p)) return p;
                // 기본 = 국내(미니) 우선, 없으면 첫 종목
                foreach (var x in _pipelines) if (!x.Cfg.Overseas) return x;
                return _pipelines.Count > 0 ? _pipelines[0] : null;
            }
        }

        public static List<InstrumentPipeline> All()
        {
            lock (_pipelines) { return new List<InstrumentPipeline>(_pipelines); }
        }

        /// <summary>이벤트 라우팅용 정확 매칭(폴백 없음) — STA hot-path에서 호출.</summary>
        public static InstrumentPipeline Exact(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            lock (_pipelines)
            {
                InstrumentPipeline p;
                return _byCode.TryGetValue(code, out p) ? p : null;
            }
        }

        public static void PushLog(string line)
        {
            _logs.Enqueue(line);
            string _;
            while (_logs.Count > 80 && _logs.TryDequeue(out _)) { }
        }

        /// <summary>/api/state?sym=CODE — 선택 종목 스냅샷 + 종목 탭 목록. camelCase 계약 유지.</summary>
        public static object State(string sym)
        {
            var r = Recorder;
            var p = Find(sym);
            var snap = p != null ? p.Features.Snapshot : null;
            var t = p != null ? p.LastTick : null;
            var q = p != null ? p.LastDepth : null;

            return new
            {
                boot = new
                {
                    loggedIn = _loggedIn,
                    loginCode = _loginCode,
                    startedAt = _bootAt.ToString("HH:mm:ss"),
                    uptimeSec = (long)(DateTime.Now - _bootAt).TotalSeconds
                },
                instruments = All().ConvertAll(x => new
                {
                    code = x.Cfg.Code,
                    name = x.Cfg.Name,
                    tickRate = x.TickRate,
                    ticks = x.TickCount
                }),
                contract = new
                {
                    futcode = p != null ? p.Cfg.Code : "",
                    name = p != null ? p.Cfg.Name : "",
                    tickSize = p != null ? p.Cfg.TickSize : 0,
                    overseas = p != null && p.Cfg.Overseas,
                    all = _miniMonths.ConvertAll(c => new { code = c.Shcode, name = c.Hname })
                },
                collect = r == null ? null : (object)new
                {
                    tickCount = p != null ? p.TickCount : 0,
                    depthCount = p != null ? p.DepthCount : 0,
                    tickRate = p != null ? p.TickRate : 0,
                    depthRate = p != null ? p.DepthRate : 0,
                    totalTicks = r.TickCount,
                    insertErrors = r.InsertErrors,
                    dbBytes = r.DbBytes()
                },
                lastTick = t == null ? null : (object)new
                {
                    chetime = t.CheTime, recvAt = t.ReceivedAt.ToString("HH:mm:ss.fff"),
                    price = t.Price, cgubun = t.CGubun, cvolume = t.CVolume, volume = t.Volume,
                    change = t.Change, drate = t.Drate, cpower = t.CPower, openyak = t.OpenYak,
                    k200jisu = t.K200Jisu, theoryprice = t.TheoryPrice, kasis = t.Kasis,
                    sbasis = t.SBasis, ibasis = t.IBasis, jgubun = t.JGubun
                },
                book = q == null ? null : (object)new
                {
                    hotime = q.HoTime, recvAt = q.ReceivedAt.ToString("HH:mm:ss.fff"),
                    offerHo = q.OfferHo, offerRem = q.OfferRem, offerCnt = q.OfferCnt,
                    bidHo = q.BidHo, bidRem = q.BidRem, bidCnt = q.BidCnt,
                    totOfferRem = q.TotOfferRem, totBidRem = q.TotBidRem, danhochk = q.DanHoChk
                },
                features = snap == null ? null : (object)new
                {
                    stamp = snap.Stamp,
                    ofi1s = snap.Ofi1s, ofi5s = snap.Ofi5s, ofi30s = snap.Ofi30s, ofi5sZ = snap.Ofi5sZ,
                    qiL1 = snap.QiL1, qiW5 = snap.QiW5,
                    mid = snap.Mid, microDevTicks = snap.MicroDevTicks,
                    spread = snap.Spread, urgency = snap.Urgency,
                    rv10 = snap.Rv10, rv10Z = snap.Rv10Z,
                    gapFlag = snap.GapFlag, kasis = snap.Kasis, basisZ = snap.BasisZ, bear = snap.Bear
                },
                signal = p == null ? null : p.Signals.GetState(),
                tape = p == null ? null : (object)p.TapeSnapshot().ConvertAll(x => new
                {
                    chetime = x.CheTime, recvAt = x.ReceivedAt.ToString("HH:mm:ss.fff"),
                    price = x.Price, cvolume = x.CVolume, cgubun = x.CGubun
                }),
                logs = _logs.ToArray()
            };
        }

        /// <summary>/api/history — 거래일 목록(최신순, 날짜당 총 청산건수/PnL).</summary>
        public static object HistoryDates()
        {
            var r = Recorder;
            var dates = r == null ? new List<FuturesRecorder.DateSummary>() : r.GetTradeDates();
            return new
            {
                dates = dates.ConvertAll(d => new { date = d.Date, pnlTicks = d.PnlTicks, closes = d.Closes })
            };
        }

        /// <summary>/api/history/day?date=YYYYMMDD — 해당 거래일의 모드별 요약 + 전 종목 체결 이력.</summary>
        public static object HistoryDay(string date)
        {
            var r = Recorder;
            if (r == null || string.IsNullOrEmpty(date))
                return new { date = date ?? "", modes = new object[0], fills = new object[0] };

            var modes = r.GetModeSummaryForDate(date);
            var fills = r.GetFillsForDate(date);
            return new
            {
                date = date,
                modes = modes.ConvertAll(m => new
                {
                    mode = m.Mode,
                    trades = m.Trades,
                    wins = m.Wins,
                    pnlTicks = m.PnlTicks,
                    winRate = m.Trades > 0 ? (100.0 * m.Wins / m.Trades) : 0.0
                }),
                fills = fills.ConvertAll(x => new
                {
                    time = x.Time, futcode = x.Code, mode = x.Mode, side = x.Side, kind = x.Kind,
                    price = x.Price, pnlTicks = x.PnlTicks, score = x.Score, reason = x.Reason
                })
            };
        }
    }

    /// <summary>OWIN 구성 — /api/state·chart(?sym=) + 정적 원스크린(wwwroot).</summary>
    public class WebStartup
    {
        public void Configuration(IAppBuilder app)
        {
            app.UseCors(CorsOptions.AllowAll);
            app.Map("/api", api => api.Run(Handle));

            var root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
            if (Directory.Exists(root))
            {
                var fs = new PhysicalFileSystem(root);
                app.UseFileServer(new FileServerOptions { EnableDefaultFiles = true, FileSystem = fs });
            }
        }

        private static async Task Handle(IOwinContext ctx)
        {
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            string path = (ctx.Request.Path.Value ?? "").TrimEnd('/').ToLowerInvariant();
            string sym = ctx.Request.Query["sym"];
            try
            {
                if (path == "/state")
                {
                    await ctx.Response.WriteAsync(JsonConvert.SerializeObject(FuturesApp.State(sym)));
                    return;
                }
                if (path == "/history")
                {
                    await ctx.Response.WriteAsync(JsonConvert.SerializeObject(FuturesApp.HistoryDates()));
                    return;
                }
                if (path == "/history/day")
                {
                    string date = ctx.Request.Query["date"];
                    await ctx.Response.WriteAsync(JsonConvert.SerializeObject(FuturesApp.HistoryDay(date)));
                    return;
                }
                if (path == "/chart")
                {
                    var r = FuturesApp.Recorder;
                    var p = FuturesApp.Find(sym);
                    var pts = (r == null || p == null)
                        ? new List<FuturesRecorder.ChartPoint>()
                        : r.ChartSnapshot(p.Cfg.Code, 1200);
                    await ctx.Response.WriteAsync(JsonConvert.SerializeObject(
                        pts.ConvertAll(x => new { t = x.T, p = x.P, k = x.K, v = x.V })));
                    return;
                }
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsync("{\"ok\":false,\"error\":\"unknown path\"}");
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsync(JsonConvert.SerializeObject(new { ok = false, error = ex.Message }));
            }
        }
    }
}
