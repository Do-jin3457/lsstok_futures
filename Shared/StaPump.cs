using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Windows.Forms;

namespace LS.Futures.Shared
{
    /// <summary>
    /// XingAPI COM 전용 STA 메시지펌프 — LS.Engine 원본에서 복사(2026-07-03, 완전 독립 방침).
    /// onStaThread를 전용 STA 스레드에서 실행(COM 객체 생성/로그인) 후, DoEvents 펌프를 유지해
    /// COM 이벤트(Login/ReceiveData/ReceiveRealData)가 이 스레드로 디스패치되게 한다.
    /// Invoke()로 외부 스레드가 STA 스레드에 작업을 마샬링할 수 있다(COM 해제는 생성 아파트먼트에서만).
    /// </summary>
    public sealed class StaPump
    {
        private Thread _thread;
        private volatile bool _stop;
        private readonly ConcurrentQueue<Action> _staWork = new ConcurrentQueue<Action>();

        public void Run(Action onStaThread)
        {
            _thread = new Thread(() =>
            {
                try { onStaThread(); }
                catch (Exception ex) { Console.WriteLine("[sta] 초기화 오류: " + ex.Message); }
                while (!_stop)
                {
                    while (_staWork.TryDequeue(out var w))
                    {
                        try { w(); } catch (Exception ex) { Console.WriteLine("[sta] 작업 오류: " + ex.Message); }
                    }
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
            })
            {
                IsBackground = true,
                Name = "futures-sta-pump"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        /// <summary>STA 스레드에서 work 실행+완료 대기. 펌프 정지/자기 스레드면 직접 실행(데드락 방지).</summary>
        public void Invoke(Action work, int timeoutMs = 10000)
        {
            if (work == null) return;
            if (_stop || _thread == null || !_thread.IsAlive) { work(); return; }
            if (Thread.CurrentThread == _thread) { work(); return; }

            var done = new ManualResetEventSlim();
            _staWork.Enqueue(() => { try { work(); } finally { done.Set(); } });
            done.Wait(timeoutMs);
        }

        public void Stop() => _stop = true;
    }
}
