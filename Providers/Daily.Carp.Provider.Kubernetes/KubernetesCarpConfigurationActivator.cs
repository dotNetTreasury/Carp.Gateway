﻿using Daily.Carp.Configuration;
using Daily.Carp.Feature;
using Daily.Carp.Yarp;
using KubeClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Reactive.Linq;
using Timer = System.Timers.Timer;

namespace Daily.Carp.Provider.Kubernetes
{
    internal class KubernetesCarpConfigurationActivator : CarpConfigurationActivator
    {
        private static readonly object lock_obj = new object();

        public KubernetesCarpConfigurationActivator(CarpProxyConfigProvider provider) : base(provider)
        {
            ConfigureServices(service => { service.AddKubeClient(true); });
            Initialize();
        }

        public override void Initialize()
        {
            Refresh();
            Watch();
            TimingUpdate();
        }

        public override void Refresh()
        {
            var carpConfig = CarpApp.GetCarpConfig();
            Inject(serviceName => GetPods(serviceName, carpConfig.Kubernetes.Namespace));
            LogInfo($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Configuration refresh.");
        }


        private void Watch()
        {
            var carpConfig = CarpApp.GetCarpConfig();
            var k8snamespace = carpConfig.Kubernetes.Namespace;
            //监听Service变化，实时更新Yarp配置
            LogInfo($"Prepare to listen to namespace {k8snamespace}.");
            var eventStream = GetService<IKubeApiClient>().PodsV1()
                .WatchAll(kubeNamespace: k8snamespace);
            eventStream.Select(resourceEvent => resourceEvent.Resource).Subscribe(
                async subsequentEvent =>
                {
                    try
                    {
                        //监听POD创建成功后，更新Config
                        var any = subsequentEvent.Status.ContainerStatuses.Any(c => c.Ready == true);
                        if (any)
                        {
                            //延迟更新Config
                            await Task.Delay(2000);
                            LogInfo("Listening to pod changes, refreshing.");
                            Refresh();
                        }
                    }
                    catch (Exception e)
                    {
                        LogError($"Listening to pod fail.{Environment.NewLine}Message: {e.Message}");
                    }
                },
                error => LogError($"Listening to pod fail.{Environment.NewLine}Message: {error.Message}"),
                () => { LogInfo("Listening to pod completed."); });
        }

        private void LogInfo(string info)
        {
            var log = CarpApp.GetRootService<ILogger<KubernetesCarpConfigurationActivator>>();
            if (log != null)
            {
                log?.LogInformation($"Carp: {info}");
            }
            else
            {
                Console.WriteLine($"Carp: {info}");
            }
        }

        public void LogError(string info)
        {
            var log = CarpApp.GetRootService<ILogger<KubernetesCarpConfigurationActivator>>();
            if (log != null)
            {
                log?.LogError($"Carp: {info}");
            }
            else
            {
                Console.WriteLine($"Carp: {info}");
            }
        }

        //为了防止其他状况 1分钟同步一次配置
        private void TimingUpdate()
        {
            Task.Run(() =>
            {
                var timer = new Timer();
                timer.Interval = 60 * 1000;
                timer.Elapsed += (sender, eventArgs) => { Refresh(); };
                timer.Start();
            });
        }

        //api server 获取pod 
        private List<Service> GetPods(string serviceName, string namespaces)
        {
            lock (lock_obj)
            {
                var services = new List<Service>();
                try
                {
                    var client = GetService<IKubeApiClient>();
                    var clientV1 = new EndPointClientV1(client: client);
                    var endpointsV1 = clientV1.Get(serviceName, namespaces).ConfigureAwait(true).GetAwaiter()
                        .GetResult();
                    var carpRouteConfig = CarpApp.GetCarpConfig().Routes.First(c => c.ServiceName == serviceName);
                    foreach (var item in endpointsV1.Subsets)
                    {
                        try
                        {
                            var port = item.Ports.First().Port;
                            foreach (var endpointAddressV1 in item.Addresses)
                            {
                                if (carpRouteConfig.Port != 0)
                                {
                                    port = carpRouteConfig.Port;
                                }

                                var host = $"{endpointAddressV1.Ip}:{port}";
                                services.Add(new Service()
                                {
                                    Host = endpointAddressV1.Ip,
                                    Port = port,
                                    Protocol = carpRouteConfig.DownstreamScheme
                                });
                            }
                        }
                        catch (Exception e)
                        {
                            LogError($"Endpoints foreach error {Environment.NewLine}Message:{e}");
                            continue;
                        }
                    }

                    LogInfo($"EndPoint {JsonConvert.SerializeObject(services)}.");
                }
                catch (Exception e)
                {
                    LogError($"Endpoints error {Environment.NewLine}Message:{e}");
                }


                return services;
            }
        }
    }
}