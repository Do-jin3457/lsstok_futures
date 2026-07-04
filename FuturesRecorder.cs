using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using LS.Futures.Models;

namespace LS.Futures
{
    /// <summary>
    /// 전량 기록기 — COM 이벤트 비차단 enqueue → 1초 flush(스레드풀) → futures_data.db(WAL).
    /// 체결 테이프 ring(UI) + 가상체결(fut_paper_fills) 영속화 포함. 독립 프로그램이라 DB는 자기 bin\Data.
    /// RecvTicks(단조시계) 원본 보존 = Phase 1 감쇠곡선의 전제(설계 D6).
    /// </summary>
    public sealed class FuturesRecorder : IDisposable
    {
        private readonly Action<string> _log;
        private readonly ConcurrentQueue<FC9Data> _tickQueue = new ConcurrentQueue<FC9Data>();
        private readonly ConcurrentQueue<FH9Data> _depthQueue = new ConcurrentQueue<FH9Data>();
        private readonly ConcurrentQueue<PaperFill> _fillQueue = new ConcurrentQueue<PaperFill>();

        private SQLiteConnection _conn;
        private Timer _flushTimer;
        private readonly object _flushLock = new object();
        private bool _disposed;

        // 다중 종목(v5): 테이프/최신값은 InstrumentPipeline이 소유 — 여기선 기록+차트샘플+전역 카운터만.
        private readonly Dictionary<string, FC9Data> _lastByCode = new Dictionary<string, FC9Data>();
        private readonly object _lastLock = new object();

        // 당일 차트용 1초 샘플 (종목별, flush 타이머에서 채집). 종목당 하루 최대 ~8.3만점(23h).
        public sealed class ChartPoint { public string T; public double P; public double K; public long V; }
        private readonly Dictionary<string, List<ChartPoint>> _chart = new Dictionary<string, List<ChartPoint>>();
        private readonly object _chartLock = new object();
        private const int ChartMax = 84000;

        /// <summary>1초마다(flush 주기) 발화 — Program이 파이프라인 rate 갱신에 사용.</summary>
        public event Action OnSecond;

        private long _tickCount, _depthCount, _insertErrors;
        private long _lastRateTicks, _lastRateDepths;
        private double _tickRate, _depthRate;
        private DateTime _startedAt;

        public string DbPath { get; private set; }

        /// <summary>거래일(trade_date) 산정 — 야간 세션(18:00~익일 06:00)이 자정을 넘겨도 한 거래일로 묶기 위해
        /// 06시 이전은 전일로 귀속(v5.3, 자정 경계 성적 분리 버그 fix). 주간·초저녁(06시~24시)은 당일.
        /// 저장(Flush)·복원(RestoreDay)·일손익 리셋(SignalEngine)이 전부 이 하나를 참조해야 경계가 어긋나지 않음.</summary>
        public static string SessionTradeDate(DateTime t)
        {
            return (t.Hour < 6 ? t.AddDays(-1) : t).ToString("yyyyMMdd");
        }

        public FuturesRecorder(Action<string> log)
        {
            _log = log ?? (_ => { });
            DbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", "futures_data.db");
        }

        public void Start()
        {
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            _conn = new SQLiteConnection($"Data Source={DbPath};Version=3;Pooling=True;Max Pool Size=4;");
            _conn.Open();
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS fut_ticks (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        trade_date TEXT NOT NULL, futcode TEXT NOT NULL,
                        chetime TEXT, recv_at TEXT, recv_ticks INTEGER,
                        price REAL, cgubun TEXT, cvolume INTEGER, volume INTEGER, value INTEGER,
                        change REAL, drate REAL, open REAL, high REAL, low REAL,
                        mdvolume INTEGER, mdchecnt INTEGER, msvolume INTEGER, mschecnt INTEGER,
                        cpower REAL, offerho1 REAL, bidho1 REAL,
                        openyak INTEGER, openyakcha INTEGER,
                        k200jisu REAL, theoryprice REAL, kasis REAL, sbasis REAL, ibasis REAL,
                        jgubun TEXT
                    );
                    CREATE INDEX IF NOT EXISTS ix_fut_ticks_date ON fut_ticks(trade_date, chetime);
                    CREATE TABLE IF NOT EXISTS fut_depth (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        trade_date TEXT NOT NULL, futcode TEXT NOT NULL,
                        hotime TEXT, recv_at TEXT, recv_ticks INTEGER,
                        offerho1 REAL, offerrem1 INTEGER, offercnt1 INTEGER, bidho1 REAL, bidrem1 INTEGER, bidcnt1 INTEGER,
                        offerho2 REAL, offerrem2 INTEGER, offercnt2 INTEGER, bidho2 REAL, bidrem2 INTEGER, bidcnt2 INTEGER,
                        offerho3 REAL, offerrem3 INTEGER, offercnt3 INTEGER, bidho3 REAL, bidrem3 INTEGER, bidcnt3 INTEGER,
                        offerho4 REAL, offerrem4 INTEGER, offercnt4 INTEGER, bidho4 REAL, bidrem4 INTEGER, bidcnt4 INTEGER,
                        offerho5 REAL, offerrem5 INTEGER, offercnt5 INTEGER, bidho5 REAL, bidrem5 INTEGER, bidcnt5 INTEGER,
                        totofferrem INTEGER, totbidrem INTEGER, totoffercnt INTEGER, totbidcnt INTEGER,
                        danhochk TEXT
                    );
                    CREATE INDEX IF NOT EXISTS ix_fut_depth_date ON fut_depth(trade_date, hotime);
                    CREATE TABLE IF NOT EXISTS fut_paper_fills (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        trade_date TEXT NOT NULL, time TEXT, mode TEXT, side TEXT, kind TEXT,
                        price REAL, pnl_ticks REAL, score REAL, reason TEXT
                    );";
                cmd.ExecuteNonQuery();
                // v5: 다중종목 체결 태그 (기존 DB 호환 — 컬럼 있으면 무시)
                try { cmd.CommandText = "ALTER TABLE fut_paper_fills ADD COLUMN futcode TEXT"; cmd.ExecuteNonQuery(); } catch { }
                // 과거 분봉(t8465) — 다장세 백테스트용. (code,date,time) 유일 = 연속조회 겹침 무시.
                cmd.CommandText = @"CREATE TABLE IF NOT EXISTS fut_minute (
                        code TEXT NOT NULL, date TEXT NOT NULL, time TEXT NOT NULL,
                        open REAL, high REAL, low REAL, close REAL,
                        volume INTEGER, value INTEGER, openyak INTEGER,
                        PRIMARY KEY(code, date, time));";
                cmd.ExecuteNonQuery();
            }

            _startedAt = DateTime.Now;
            _flushTimer = new Timer(delegate { Flush(); }, null, 1000, 1000);
            _log($"[recorder] 기록 시작 — db={DbPath}");
        }

        // ── 수신부 (STA 스레드, 절대 비차단) ──

        public void OnTick(FC9Data d)
        {
            if (d == null) return;
            Interlocked.Increment(ref _tickCount);
            _tickQueue.Enqueue(d);
            lock (_lastLock) { _lastByCode[d.Futcode ?? ""] = d; }
        }

        public void OnDepth(FH9Data d)
        {
            if (d == null) return;
            Interlocked.Increment(ref _depthCount);
            _depthQueue.Enqueue(d);
        }

        public void OnPaperFill(PaperFill f)
        {
            if (f != null) _fillQueue.Enqueue(f);
        }

        // ── 1초 배치 flush ──

        private void Flush()
        {
            if (_disposed || _conn == null) return;
            if (!Monitor.TryEnter(_flushLock)) return;
            try
            {
                string tradeDate = SessionTradeDate(DateTime.Now);   // v5.3: 야간 자정 넘김을 한 거래일로
                using (var tx = _conn.BeginTransaction())
                {
                    FlushTicks(tx, tradeDate);
                    FlushDepth(tx, tradeDate);
                    FlushFills(tx, tradeDate);
                    tx.Commit();
                }
                long tc = Interlocked.Read(ref _tickCount), dc = Interlocked.Read(ref _depthCount);
                _tickRate = tc - _lastRateTicks;
                _depthRate = dc - _lastRateDepths;
                _lastRateTicks = tc; _lastRateDepths = dc;

                // 차트 1초 샘플 — 종목별 최신 체결 기준
                List<FC9Data> lasts;
                lock (_lastLock) { lasts = new List<FC9Data>(_lastByCode.Values); }
                string nowStr = DateTime.Now.ToString("HH:mm:ss");
                lock (_chartLock)
                {
                    foreach (var lt in lasts)
                    {
                        if (lt == null || lt.Price <= 0) continue;
                        List<ChartPoint> series;
                        if (!_chart.TryGetValue(lt.Futcode ?? "", out series))
                        {
                            series = new List<ChartPoint>();
                            _chart[lt.Futcode ?? ""] = series;
                        }
                        series.Add(new ChartPoint { T = nowStr, P = (double)lt.Price, K = (double)lt.K200Jisu, V = lt.Volume });
                        if (series.Count > ChartMax) series.RemoveRange(0, series.Count - ChartMax);
                    }
                }
                var sec = OnSecond; if (sec != null) { try { sec(); } catch { } }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _insertErrors);
                _log("[recorder] flush 오류: " + ex.Message);
            }
            finally { Monitor.Exit(_flushLock); }
        }

        private void FlushTicks(SQLiteTransaction tx, string tradeDate)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO fut_ticks
                    (trade_date,futcode,chetime,recv_at,recv_ticks,price,cgubun,cvolume,volume,value,
                     change,drate,open,high,low,mdvolume,mdchecnt,msvolume,mschecnt,cpower,
                     offerho1,bidho1,openyak,openyakcha,k200jisu,theoryprice,kasis,sbasis,ibasis,jgubun)
                    VALUES (@d,@f,@ct,@ra,@rt,@p,@cg,@cv,@v,@val,@ch,@dr,@o,@h,@l,@mdv,@mdc,@msv,@msc,@cp,@oh,@bh,@oy,@oyc,@k2,@tp,@ka,@sb,@ib,@jg)";
                foreach (var name in new[] { "@d","@f","@ct","@ra","@rt","@p","@cg","@cv","@v","@val","@ch","@dr","@o","@h","@l","@mdv","@mdc","@msv","@msc","@cp","@oh","@bh","@oy","@oyc","@k2","@tp","@ka","@sb","@ib","@jg" })
                    cmd.Parameters.Add(new SQLiteParameter(name));
                var P = cmd.Parameters;
                FC9Data t;
                while (_tickQueue.TryDequeue(out t))
                {
                    P["@d"].Value = tradeDate; P["@f"].Value = t.Futcode; P["@ct"].Value = t.CheTime;
                    P["@ra"].Value = t.ReceivedAt.ToString("HH:mm:ss.fff"); P["@rt"].Value = t.RecvTicks;
                    P["@p"].Value = t.Price; P["@cg"].Value = t.CGubun; P["@cv"].Value = t.CVolume;
                    P["@v"].Value = t.Volume; P["@val"].Value = t.Value; P["@ch"].Value = t.Change;
                    P["@dr"].Value = t.Drate; P["@o"].Value = t.Open; P["@h"].Value = t.High; P["@l"].Value = t.Low;
                    P["@mdv"].Value = t.MdVolume; P["@mdc"].Value = t.MdCheCnt; P["@msv"].Value = t.MsVolume;
                    P["@msc"].Value = t.MsCheCnt; P["@cp"].Value = t.CPower; P["@oh"].Value = t.OfferHo1;
                    P["@bh"].Value = t.BidHo1; P["@oy"].Value = t.OpenYak; P["@oyc"].Value = t.OpenYakCha;
                    P["@k2"].Value = t.K200Jisu; P["@tp"].Value = t.TheoryPrice; P["@ka"].Value = t.Kasis;
                    P["@sb"].Value = t.SBasis; P["@ib"].Value = t.IBasis; P["@jg"].Value = t.JGubun;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void FlushDepth(SQLiteTransaction tx, string tradeDate)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO fut_depth
                    (trade_date,futcode,hotime,recv_at,recv_ticks,
                     offerho1,offerrem1,offercnt1,bidho1,bidrem1,bidcnt1,
                     offerho2,offerrem2,offercnt2,bidho2,bidrem2,bidcnt2,
                     offerho3,offerrem3,offercnt3,bidho3,bidrem3,bidcnt3,
                     offerho4,offerrem4,offercnt4,bidho4,bidrem4,bidcnt4,
                     offerho5,offerrem5,offercnt5,bidho5,bidrem5,bidcnt5,
                     totofferrem,totbidrem,totoffercnt,totbidcnt,danhochk)
                    VALUES (@d,@f,@ht,@ra,@rt,
                     @oh1,@or1,@oc1,@bh1,@br1,@bc1, @oh2,@or2,@oc2,@bh2,@br2,@bc2,
                     @oh3,@or3,@oc3,@bh3,@br3,@bc3, @oh4,@or4,@oc4,@bh4,@br4,@bc4,
                     @oh5,@or5,@oc5,@bh5,@br5,@bc5, @tor,@tbr,@toc,@tbc,@dan)";
                foreach (var name in new[] { "@d", "@f", "@ht", "@ra", "@rt", "@tor", "@tbr", "@toc", "@tbc", "@dan" })
                    cmd.Parameters.Add(new SQLiteParameter(name));
                for (int i = 1; i <= 5; i++)
                {
                    cmd.Parameters.Add(new SQLiteParameter("@oh" + i)); cmd.Parameters.Add(new SQLiteParameter("@or" + i)); cmd.Parameters.Add(new SQLiteParameter("@oc" + i));
                    cmd.Parameters.Add(new SQLiteParameter("@bh" + i)); cmd.Parameters.Add(new SQLiteParameter("@br" + i)); cmd.Parameters.Add(new SQLiteParameter("@bc" + i));
                }
                var P = cmd.Parameters;
                FH9Data q;
                while (_depthQueue.TryDequeue(out q))
                {
                    P["@d"].Value = tradeDate; P["@f"].Value = q.Futcode; P["@ht"].Value = q.HoTime;
                    P["@ra"].Value = q.ReceivedAt.ToString("HH:mm:ss.fff"); P["@rt"].Value = q.RecvTicks;
                    for (int i = 0; i < 5; i++)
                    {
                        string n = (i + 1).ToString();
                        P["@oh" + n].Value = q.OfferHo[i]; P["@or" + n].Value = q.OfferRem[i]; P["@oc" + n].Value = q.OfferCnt[i];
                        P["@bh" + n].Value = q.BidHo[i]; P["@br" + n].Value = q.BidRem[i]; P["@bc" + n].Value = q.BidCnt[i];
                    }
                    P["@tor"].Value = q.TotOfferRem; P["@tbr"].Value = q.TotBidRem;
                    P["@toc"].Value = q.TotOfferCnt; P["@tbc"].Value = q.TotBidCnt; P["@dan"].Value = q.DanHoChk;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        private void FlushFills(SQLiteTransaction tx, string tradeDate)
        {
            using (var cmd = _conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO fut_paper_fills (trade_date,time,mode,side,kind,price,pnl_ticks,score,reason,futcode)
                                    VALUES (@d,@t,@m,@s,@k,@p,@pt,@sc,@r,@fc)";
                foreach (var name in new[] { "@d", "@t", "@m", "@s", "@k", "@p", "@pt", "@sc", "@r", "@fc" })
                    cmd.Parameters.Add(new SQLiteParameter(name));
                var P = cmd.Parameters;
                PaperFill f;
                while (_fillQueue.TryDequeue(out f))
                {
                    P["@d"].Value = tradeDate; P["@t"].Value = f.Time; P["@m"].Value = f.Mode;
                    P["@s"].Value = f.Side; P["@k"].Value = f.Kind; P["@p"].Value = f.Price;
                    P["@pt"].Value = f.PnlTicks; P["@sc"].Value = f.Score; P["@r"].Value = f.Reason; P["@fc"].Value = f.Code;
                    cmd.ExecuteNonQuery();
                }
            }
        }

        // ── 상태 스냅샷 (웹 스레드에서 호출) ──

        public long TickCount { get { return Interlocked.Read(ref _tickCount); } }
        public long DepthCount { get { return Interlocked.Read(ref _depthCount); } }
        public double TickRate { get { return _tickRate; } }
        public double DepthRate { get { return _depthRate; } }
        public long InsertErrors { get { return Interlocked.Read(ref _insertErrors); } }
        public DateTime StartedAt { get { return _startedAt; } }

        /// <summary>
        /// 재시작 정합성(v5.1): 당일 확정 성적 로드 + 고아 ENTRY(청산 없이 프로세스 종료) 'RESTART' 마감.
        /// 반환: futcode → [pnlTicks 합, 청산 수, 승 수]. STA 부팅 시 1회 호출(flush 타이머 시작 전).
        /// </summary>
        public Dictionary<string, double[]> RestoreDay(string tradeDate)
        {
            var result = new Dictionary<string, double[]>();
            Monitor.Enter(_flushLock);   // flush 타이머와 _conn 경합 방지
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    // 고아 ENTRY 마감: 종목별 ENTRY 수 > 청산 수 만큼 RESTART 행 삽입(pnl=0 — 미확정분 성적 미반영).
                    // ⚠️ 청산 수에 RESTART도 포함해야 함(v5.3 fix) — 안 그러면 이전 RESTART가 마감한 ENTRY가
                    //    매 재시작마다 미청산으로 부활해 유령 RESTART가 무한 누적됨(회원 지적 7/4, "?" 3개 버그).
                    cmd.CommandText = @"SELECT COALESCE(futcode,''),
                                           SUM(CASE WHEN kind='ENTRY' THEN 1 ELSE 0 END),
                                           SUM(CASE WHEN kind!='ENTRY' THEN 1 ELSE 0 END)
                                        FROM fut_paper_fills WHERE trade_date=@d GROUP BY COALESCE(futcode,'')";
                    cmd.Parameters.Add(new SQLiteParameter("@d", tradeDate));
                    var orphans = new List<string>();
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read())
                        {
                            long entries = rd.GetInt64(1), exits = rd.GetInt64(2);
                            for (long i = exits; i < entries; i++) orphans.Add(rd.GetString(0));
                        }
                    foreach (var code in orphans)
                    {
                        using (var ins = _conn.CreateCommand())
                        {
                            ins.CommandText = @"INSERT INTO fut_paper_fills (trade_date,time,mode,side,kind,price,pnl_ticks,score,reason,futcode)
                                                VALUES (@d,@t,'V1','?','RESTART',0,0,0,'재시작 고아 ENTRY 마감',@fc)";
                            ins.Parameters.Add(new SQLiteParameter("@d", tradeDate));
                            ins.Parameters.Add(new SQLiteParameter("@t", DateTime.Now.ToString("HH:mm:ss.fff")));
                            ins.Parameters.Add(new SQLiteParameter("@fc", code));
                            ins.ExecuteNonQuery();
                        }
                        _log($"[recorder] 재시작 정합: {code} 고아 ENTRY → RESTART 마감");
                    }

                    // 당일 확정 성적 로드
                    using (var q = _conn.CreateCommand())
                    {
                        // 거래수/pnl은 실제 청산(TIME/DISASTER/EOD)만 — ENTRY와 RESTART(시스템 마커)는 제외(v5.3 fix).
                        q.CommandText = @"SELECT COALESCE(futcode,''), SUM(pnl_ticks), COUNT(*),
                                             SUM(CASE WHEN pnl_ticks>0 THEN 1 ELSE 0 END)
                                          FROM fut_paper_fills
                                          WHERE trade_date=@d AND kind NOT IN ('ENTRY','RESTART') GROUP BY COALESCE(futcode,'')";
                        q.Parameters.Add(new SQLiteParameter("@d", tradeDate));
                        using (var rd = q.ExecuteReader())
                            while (rd.Read())
                                result[rd.GetString(0)] = new[] { rd.IsDBNull(1) ? 0 : rd.GetDouble(1), (double)rd.GetInt64(2), (double)rd.GetInt64(3) };
                    }
                }
            }
            catch (Exception ex) { _log("[recorder] 재시작 정합 오류: " + ex.Message); }
            finally { Monitor.Exit(_flushLock); }
            return result;
        }

        /// <summary>재시작 시 대시보드 체결내역 복원용(v5.2) — 종목별 당일 최근 fills를 시간순으로 반환.
        /// SignalEngine._fills가 in-memory ring이라 재기동 시 증발 → 이걸로 재적재. 부팅 시 1회(flush 시작 전).</summary>
        public List<PaperFill> LoadRecentFills(string futcode, string tradeDate, int limit)
        {
            var list = new List<PaperFill>();
            Monitor.Enter(_flushLock);
            try
            {
                using (var cmd = _conn.CreateCommand())
                {
                    // RESTART(시스템 마커)는 대시보드 체결 이력에서 제외 — 매매가 아니므로 "?" 행 노출 방지(v5.3, 회원 지적 7/4).
                    cmd.CommandText = @"SELECT time,mode,side,kind,price,pnl_ticks,score,reason
                                        FROM fut_paper_fills
                                        WHERE trade_date=@d AND COALESCE(futcode,'')=@fc AND kind!='RESTART'
                                        ORDER BY id DESC LIMIT @lim";
                    cmd.Parameters.Add(new SQLiteParameter("@d", tradeDate));
                    cmd.Parameters.Add(new SQLiteParameter("@fc", futcode ?? ""));
                    cmd.Parameters.Add(new SQLiteParameter("@lim", limit));
                    using (var rd = cmd.ExecuteReader())
                        while (rd.Read())
                            list.Add(new PaperFill
                            {
                                Code = futcode,
                                Time = rd.IsDBNull(0) ? "" : rd.GetString(0),
                                Mode = rd.IsDBNull(1) ? "" : rd.GetString(1),
                                Side = rd.IsDBNull(2) ? "" : rd.GetString(2),
                                Kind = rd.IsDBNull(3) ? "" : rd.GetString(3),
                                Price = rd.IsDBNull(4) ? 0 : rd.GetDouble(4),
                                PnlTicks = rd.IsDBNull(5) ? 0 : rd.GetDouble(5),
                                Score = rd.IsDBNull(6) ? 0 : rd.GetDouble(6),
                                Reason = rd.IsDBNull(7) ? "" : rd.GetString(7)
                            });
                }
                list.Reverse();   // id DESC로 읽었으니 시간순으로 뒤집기
            }
            catch (Exception ex) { _log("[recorder] fills 로드 오류: " + ex.Message); }
            finally { Monitor.Exit(_flushLock); }
            return list;
        }

        /// <summary>t8465 과거 분봉 배치 저장 — (code,date,time) 중복은 무시(연속조회 페이지 겹침 대비).</summary>
        public void SaveMinBars(List<FutMinBar> bars)
        {
            if (bars == null || bars.Count == 0 || _conn == null) return;
            Monitor.Enter(_flushLock);
            try
            {
                using (var tx = _conn.BeginTransaction())
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO fut_minute
                        (code,date,time,open,high,low,close,volume,value,openyak)
                        VALUES (@c,@d,@t,@o,@h,@l,@cl,@v,@val,@oy)";
                    foreach (var n in new[] { "@c","@d","@t","@o","@h","@l","@cl","@v","@val","@oy" })
                        cmd.Parameters.Add(new SQLiteParameter(n));
                    var P = cmd.Parameters;
                    foreach (var b in bars)
                    {
                        P["@c"].Value = b.Code; P["@d"].Value = b.Date; P["@t"].Value = b.Time;
                        P["@o"].Value = (double)b.Open; P["@h"].Value = (double)b.High;
                        P["@l"].Value = (double)b.Low; P["@cl"].Value = (double)b.Close;
                        P["@v"].Value = b.Volume; P["@val"].Value = b.Value; P["@oy"].Value = b.OpenYak;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
            catch (Exception ex) { _log("[recorder] 분봉 저장 오류: " + ex.Message); }
            finally { Monitor.Exit(_flushLock); }
        }

        public long DbBytes()
        {
            try { return File.Exists(DbPath) ? new FileInfo(DbPath).Length : 0; } catch { return 0; }
        }

        /// <summary>당일 차트 시리즈(종목별) — maxPoints 초과 시 균등 다운샘플(전송량 고정).</summary>
        public List<ChartPoint> ChartSnapshot(string futcode, int maxPoints)
        {
            lock (_chartLock)
            {
                List<ChartPoint> src;
                if (!_chart.TryGetValue(futcode ?? "", out src)) return new List<ChartPoint>();
                int n = src.Count;
                if (n <= maxPoints) return new List<ChartPoint>(src);
                var outp = new List<ChartPoint>(maxPoints);
                double step = (double)n / maxPoints;
                for (int i = 0; i < maxPoints; i++) outp.Add(src[(int)(i * step)]);
                outp[outp.Count - 1] = src[n - 1];
                return outp;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { if (_flushTimer != null) _flushTimer.Dispose(); } catch { }
            try { Monitor.Enter(_flushLock); Monitor.Exit(_flushLock); } catch { }   // 진행 중 flush 대기
            try { _disposed = false; Flush(); } finally { _disposed = true; }        // 잔여 큐 마지막 flush
            try { if (_conn != null) { _conn.Close(); _conn.Dispose(); } } catch { }
            _log($"[recorder] 종료 — ticks={_tickCount} depths={_depthCount} errors={_insertErrors}");
        }
    }
}
