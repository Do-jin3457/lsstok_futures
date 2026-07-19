using System;
using System.Collections.Generic;
using LS.Futures.Models;

namespace LS.Futures
{
    /// <summary>t8465 과거 분봉 1개 — 다장세 백테스트용(2026-07-04). OHLC + 미결제약정.</summary>
    public sealed class FutMinBar
    {
        public string Code, Date, Time;
        public decimal Open, High, Low, Close;
        public long Volume, Value, OpenYak;
    }

    /// <summary>종목별 스펙/세션 설정 — 다중 종목 확장(2026-07-04, 미니200+MES+MNQ).</summary>
    public sealed class InstrumentConfig
    {
        public string Code;          // A0567000 / MESU26 / MNQU26
        public string Name;
        public double TickSize;      // 미니 0.02 / MES·MNQ 0.25
        public double TickValueKrw;  // 미니 1,000원 / MES $1.25≈1,750원 / MNQ $0.50≈700원 (환율 1400 가정 라벨)
        public double CommissionRt;  // 왕복 정액 수수료(원) — 해외 전용($7.5×2×환율). 정률 종목은 0.
        public double CommissionPct; // 편도 정률(약정금액 대비) — 미니 KOSPI200 0.00003(=0.003%, 주간·KRX야간 동일). 정액이면 0.
        public bool Overseas;        // 세션 규칙: false=KRX(주간+야간) / true=CME(07:00~익일 06:00)

        // ── 추세추종 v1/v2 전략 파라미터 (종목별 최적화용, 2026-07-04 config화) ──
        // 로직은 전 종목 동일, 파라미터만 시장 성격에 맞춰 조정. 기본값=미니 KOSPI200 9일 in-sample(룰8).
        public double TrThrFrac = 0.30 / 1300.0;  // 10분 모멘텀 진입 임계(현재가 비례): thr = mid × frac
        public int TrHoldMin = 10;                // 최대 보유(분)
        public double TrErThr = 0.35;             // ER 횡보 판별 임계(이하면 진입 스킵)
        public double TrKAtr = 3.5;               // 손절 폭 = ATR × 배수
    }

    /// <summary>
    /// 종목 1개의 처리 파이프라인 — 피처/신호/테이프/카운터를 종목별로 독립 보유.
    /// STA 이벤트 스레드에서만 쓰기, 웹은 참조 읽기(volatile 교체).
    /// </summary>
    public sealed class InstrumentPipeline
    {
        public readonly InstrumentConfig Cfg;
        public readonly FeatureEngine Features;
        public readonly SignalEngine Signals;

        private volatile FC9Data _lastTick;
        private volatile FH9Data _lastDepth;
        private readonly List<FC9Data> _tape = new List<FC9Data>();
        private const int TapeMax = 40;

        private long _tickCount, _depthCount;
        private long _lastRateT, _lastRateD;
        public double TickRate, DepthRate;

        private long _lastTickMs;   // 마지막 체결 수신(단조시계) — 무수신 감지(조기폐장/중단)용
        private static long NowMs() { return System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000); }

        public InstrumentPipeline(InstrumentConfig cfg, Action<string> log)
        {
            Cfg = cfg;
            Features = new FeatureEngine(cfg.TickSize);
            Signals = new SignalEngine(log, cfg);
            _lastTickMs = NowMs();   // 부팅 직후 무수신 오판 방지
        }

        public void OnTick(FC9Data d)
        {
            _lastTick = d;
            _lastTickMs = NowMs();
            System.Threading.Interlocked.Increment(ref _tickCount);
            _tape.Add(d);
            if (_tape.Count > TapeMax) _tape.RemoveAt(0);
            Features.OnTick(d);
            Signals.OnTickRef((double)d.Price, (double)d.Drate);   // GAP 전일종가 역산 — drate 기반(7/14 부호버그 fix, change는 KRX서 무부호)
            Signals.OnTickFlow(d.CGubun, d.CVolume);               // DFADE 120s 수급델타(7/15 — 체결 공격방향)
        }

        public void OnDepth(FH9Data d)
        {
            _lastDepth = d;
            System.Threading.Interlocked.Increment(ref _depthCount);
            Features.OnDepth(d);
            Signals.Step(Features.Snapshot);
        }

        /// <summary>1초 주기 rate 갱신 + 무수신 하트비트(recorder flush에서 호출). depth가 멈춰도 이건 계속 돌아 조기폐장 청산 가능.</summary>
        public void TickRates()
        {
            long t = System.Threading.Interlocked.Read(ref _tickCount);
            long q = System.Threading.Interlocked.Read(ref _depthCount);
            TickRate = t - _lastRateT; DepthRate = q - _lastRateD;
            _lastRateT = t; _lastRateD = q;

            double sinceTickSec = (NowMs() - _lastTickMs) / 1000.0;
            Signals.OnHeartbeat(sinceTickSec, Features.Snapshot);
        }

        public FC9Data LastTick { get { return _lastTick; } }
        public FH9Data LastDepth { get { return _lastDepth; } }
        public long TickCount { get { return System.Threading.Interlocked.Read(ref _tickCount); } }
        public long DepthCount { get { return System.Threading.Interlocked.Read(ref _depthCount); } }

        public List<FC9Data> TapeSnapshot()
        {
            var list = new List<FC9Data>();
            var src = _tape;
            for (int i = src.Count - 1; i >= 0 && list.Count < TapeMax; i--)
            {
                try { list.Add(src[i]); } catch { break; }
            }
            return list;
        }
    }
}
