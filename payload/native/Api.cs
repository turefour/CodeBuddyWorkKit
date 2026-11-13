using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace WorkKit.Native
{
    /// <summary>
    /// 极简 HTTP + JSON 客户端。只用 .NET 自带的 HttpWebRequest 与
    /// JavaScriptSerializer，不引入任何第三方库 —— 部署包必须能在离线机器上跑。
    /// </summary>
    internal static class Api
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        internal static Dictionary<string, object> GetObject(string url, int timeoutMs)
        {
            object parsed = GetJson(url, timeoutMs);
            return parsed as Dictionary<string, object>;
        }

        internal static object GetJson(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Proxy = null;
                request.KeepAlive = false;

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    if (string.IsNullOrEmpty(text)) return null;
                    return Serializer.Deserialize<object>(text.TrimStart('\uFEFF'));
                }
            }
            catch
            {
                return null;
            }
        }

        internal static bool GetOk(string url, int timeoutMs)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = timeoutMs;
                request.Proxy = null;
                request.KeepAlive = false;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static bool PostObject(string url, object payload, int timeoutMs)
        {
            try
            {
                string body = Serializer.Serialize(payload);
                byte[] data = Encoding.UTF8.GetBytes(body);

                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = timeoutMs;
                request.Proxy = null;
                request.ContentLength = data.Length;

                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------------------------------------------------------- 取值助手

        internal static List<object> AsList(object value)
        {
            List<object> result = new List<object>();
            if (value == null) return result;

            ArrayList array = value as ArrayList;
            if (array != null)
            {
                foreach (object item in array) result.Add(item);
                return result;
            }
            object[] objects = value as object[];
            if (objects != null)
            {
                foreach (object item in objects) result.Add(item);
                return result;
            }
            Dictionary<string, object> single = value as Dictionary<string, object>;
            if (single != null) result.Add(single);
            return result;
        }

        internal static string Str(Dictionary<string, object> map, string key, string fallback)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return fallback;
            return Convert.ToString(map[key]);
        }

        internal static int Int(Dictionary<string, object> map, string key, int fallback)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return fallback;
            int value;
            return int.TryParse(Convert.ToString(map[key]), out value) ? value : fallback;
        }

        internal static double Dbl(Dictionary<string, object> map, string key, double fallback)
        {
            if (map == null || !map.ContainsKey(key) || map[key] == null) return fallback;
            double value;
            return double.TryParse(Convert.ToString(map[key]), out value) ? value : fallback;
        }

        internal static Dictionary<string, object> Map(Dictionary<string, object> map, string key)
        {
            if (map == null || !map.ContainsKey(key)) return null;
            return map[key] as Dictionary<string, object>;
        }
    }
}
