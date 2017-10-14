﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using log4net;
using LwSoft;
using WxTools.Annotations;
using WxTools.Client.Dal;
using WxTools.Client.Helper;
using WxTools.Client.Model;
using WxTools.Theme;

namespace WxTools.Client.ViewModel
{
    public class MainViewModel : INotifyPropertyChanged
    {
        #region 字段

        public static MainViewModel Instance { get; }  = new MainViewModel();

        private readonly ILog _log = LogManager.GetLogger(typeof(MainViewModel));

        private readonly object _lock = new object();

        private readonly Queue<string> _urlQueue = new Queue<string>();

        private DateTime _lasTime;

        private bool _runstate;

        private bool _isExit;

        public TcpClientDal TcpClientDal { get; } = new TcpClientDal();

        #endregion

        #region 属性

        private ObservableCollection<OperaDal> _operas;

        public ObservableCollection<OperaDal> Operas
        {
            get => _operas;
            set
            {
                if (Equals(value, _operas)) return;
                _operas = value;
                OnPropertyChanged();
            }
        }

        #endregion

        #region 构造函数

        public MainViewModel()
        {
            Operas = new ObservableCollection<OperaDal>();
            RegisterMessenger();

            //开启线程
            StartCheckUpdateThread();
            TcpClientDal.ConnectedAction = () =>
            {
                //只运行一次
                if (_runstate) return;
                _runstate = true;
                StartUrlQueueThread();
                StartCheckStateThread();
            };
            TcpClientDal.Connect();
        }

        #endregion

        #region 命令

        /// <summary>
        /// 窗体关闭事件
        /// </summary>
        public RelayCommand ClosedCommand => new RelayCommand(() =>
        {
            _isExit = true;
            TcpClientDal.Dispose();
            Operas.Clear();
            LwFactory.Clear();
        });

        public RelayCommand OpenWeixinCommand => new RelayCommand(() =>
        {
            var path = @"C:\Program Files (x86)\Tencent\WeChat\WeChat.exe";
            if (File.Exists(path))
            {
                try
                {
                    Process myProcess = new Process();
                    ProcessStartInfo myProcessStartInfo = new ProcessStartInfo("duokai.exe")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };
                    myProcess.StartInfo = myProcessStartInfo;
                    myProcess.Start();
                    myProcess.Close();
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        });

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler PropertyChanged;

        [NotifyPropertyChangedInvocator]
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion

        #region 方法

        //执行链接操作
        public void ExecuteUrl(string url)
        {
            _urlQueue.Enqueue(url);
        }

        //注册消息监听
        private void RegisterMessenger()
        {
            Common.Messenger.Register("CefWebViewWnd", () =>
            {
                _lasTime = DateTime.Now;
                Common.SessionCount += 1;

                if (Common.SessionCount >= Common.MaxSessionCount || Common.SessionCount == Operas.Count)
                {
                    Common.RunState = RunState.Busy;
                    _log.Info($"清理一次窗口; SessionCount:{Common.SessionCount};" +
                              $" Operas.Count:{Operas.Count}; MaxSessionCount:{Common.MaxSessionCount};");
                    //超过窗口数 或者 正好
                    Thread.Sleep(4000);
                    CloseCefWebViewWnd();
                    Common.SessionCount = 0;
                    Common.RunState = RunState.Idle;
                }
            });
        }

        //关闭CefWebViewWnd窗口
        private void CloseCefWebViewWnd()
        {
            var hwndstr = LwFactory.Default.EnumWindow("微信", "CefWebViewWnd", null);
            if (hwndstr != null)
            {
                var hwnds = hwndstr.Split(',');
                foreach (var hwnd in hwnds)
                {
                    if (!String.IsNullOrEmpty(hwnd))
                        WinApi.SendMessage(new IntPtr(int.Parse(hwnd)), 0x0010, 0, 0);
                }
            }
        }

        private OperaDal CreateAndLoadOperaDal(Lwsoft3 lw, int hwnd)
        {
            var opera = new OperaDal(lw) { Hwnd = hwnd };
            opera.Load();
            return opera;
        }

        private int[] EnumTargetWindow()
        {
            var hwndstr = LwFactory.Default.EnumWindow(null, "WeChatMainWndForPC", null);
            //var hwndstr = LwFactory.Default.EnumWindow(null, "Notepad", null);
            var hwnds = hwndstr?.Split(',');
            if (hwnds?.Length > 0)
            {
                var ints = new int[hwnds.Length];
                for (var i = 0; i < hwnds.Length; i++)
                    ints[i] = int.Parse(hwnds[i]);
                return ints;
            }
            return new int[0];
        }

        #region 线程

        //监测窗体，变量状态 线程
        private void StartCheckStateThread()
        {
            new Thread(() =>
            {
                _log.Info("监测线程启动成功");
                Thread.Sleep(2000);
                var list = new List<OperaDal>();
                while (!_isExit)
                {
                    Thread.Sleep(2000);
                    //服务器连接成功时才进行这些检查
                    if (TcpClientDal.Connected)
                    {
                        bool wxAddOrRemove = false;
                        foreach (var opera in Operas)
                        {
                            if (opera.Hwnd == 0)
                                _log.Error("句柄为0");
                            if (opera.Hwnd != 0 && opera.Lw.GetWindowState(opera.Hwnd, 0) == 0)
                            {
                                //窗体不存在
                                if (String.IsNullOrEmpty(opera.Lw.GetWindowClass(opera.Hwnd)))
                                {
                                    list.Add(opera);
                                }
                            }
                            else
                            {
                                if (opera.Lw.GetWindowState(opera.Hwnd, 3) == 1)
                                {
                                    //窗体最小化了
                                    _log.Warn($"微信被窗体最小化了，已经恢复，hwnd={opera.Hwnd}");
                                    opera.Lw.SetWindowState(opera.Hwnd, 7);
                                }
                                if (opera.Lw.GetClientSize(opera.Hwnd) == 1)
                                {
                                    if (opera.Lw.X() != Common.Width || opera.Lw.Y() != Common.Height)
                                    {
                                        opera.Lw.SetWindowSize(opera.Hwnd, Common.Width, Common.Height);
                                        _log.Warn("微信窗体大小被用户拖动，已经恢复");
                                    }
                                }
                            }
                        }
                        foreach (var dal in list)
                        {
                            _log.Warn($"微信窗口不存在，hwnd={dal.Hwnd}");
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                lock (Operas)
                                    Operas.Remove(dal);
                                dal.Dispose();
                                wxAddOrRemove = true;
                            });
                        }

                        //防止文章窗口打开过久
                        if (Common.SessionCount > 0 && (DateTime.Now - _lasTime).Seconds > 30)
                        {
                            _log.Warn("文章窗口打开过久，已经关闭");
                            CloseCefWebViewWnd();
                            Common.SessionCount = 0;
                        }

                        #region 新的微信

                        var hwnds = EnumTargetWindow();
                        if (hwnds.Length > Operas.Count)
                        {
                            foreach (var hwnd in hwnds)
                            {
                                if (Operas.All(o => o.Hwnd != hwnd))
                                {
                                    wxAddOrRemove = true;
                                    _log.Info("新的微信");
                                    var newlw = Operas.Count == 0 ? LwFactory.Default : LwFactory.GetNew();
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        try
                                        {
                                            lock (Operas)
                                                Operas.Add(CreateAndLoadOperaDal(newlw, hwnd));
                                        }
                                        catch (Exception e)
                                        {
                                            _log.Error($"微信窗口[{hwnd}]加载出错", e);
                                            MessageBox.Show($"微信窗口[{hwnd}]加载出错", "提示");
                                        }
                                    });
                                }
                            }
                        }

                        #endregion

                        if (_isExit) return;
                        if (TcpClientDal.CheckIsConnected())
                        {
                            //心跳包
                            TcpClientDal.SendHeartbeat();
                        }
                        else
                        {
                            //服务器被断开
                            TcpClientDal.Connect();
                            Thread.Sleep(5000);
                        }

                        //通知服务器微信数量
                        if (wxAddOrRemove)
                        {
                            TcpClientDal.SendWxCount(Operas.Count);
                        }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "检测状态线程"
            }.Start();
        }

        //检测程序更新
        private void StartCheckUpdateThread()
        {
            new Thread(() =>
            {
                while (!_isExit)
                {
                    if (Operas != null && Operas.All(o => o.RunState == RunState.Idle))
                    {
                        Common.StartUpdate();
                    }
                    Thread.Sleep(60000);
                }
            }) {IsBackground = true}.Start();
        }

        //链接队列处理线程
        private void StartUrlQueueThread()
        {
            new Thread(() =>
                {
                    while (!_isExit)
                    {
                        if (_urlQueue.Count > 0)
                        {
                            var url = _urlQueue.Dequeue();
                            int index = -1;
                            var max = Operas.Count < Common.MaxThreadCount ? Operas.Count : Common.MaxThreadCount;

                            Task[] tasks = new Task[max];
                            for (int i = 0; i < tasks.Length; i++)
                            {
                                tasks[i] = Task.Factory.StartNew(() =>
                                {
                                    while (true)
                                    {
                                        OperaDal opear;
                                        int ic;
                                        lock (_lock)
                                        {
                                            index++;
                                            if (index >= Operas.Count) return;
                                            _log.Info($"ExecuteUrl Operas[{index}]开始执行");
                                            opear = Operas[index];
                                            ic = index;
                                        }
                                        opear?.SendMyMessage(url, ic);
                                    }
                                });
                            }
                            Task.WaitAll(tasks);
                            _log.Info("--全部完成--");
                            TcpClientDal.SendLog("--全部完成--");
                        }
                        Thread.Sleep(50);
                    }
                })
                {IsBackground = true, Name = "Url执行线程"}.Start();
        }

        #endregion

        #endregion
    }
}
