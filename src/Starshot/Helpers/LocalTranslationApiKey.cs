using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Starshot.Helpers;

internal static class LocalTranslationApiKey
{
    private static readonly object Gate=new();
    private static string KeyPath=>Path.Combine(AppConfig.UserDataFolder,"local-translation-api.dpapi");
    internal static string ReadOrCreate()
    {
        lock(Gate)
        {
            if(File.Exists(KeyPath))
            {
                byte[] plain=OcrTranslationClient.Unprotect(File.ReadAllBytes(KeyPath));
                try
                {
                    string value=Encoding.UTF8.GetString(plain);
                    if(value.Length!=64 || !System.Linq.Enumerable.All(value,Uri.IsHexDigit))throw new InvalidDataException("本地 API 密钥文件无效。");
                    return value;
                }
                finally{CryptographicOperations.ZeroMemory(plain);}
            }
            string key=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            byte[] encrypted=OcrTranslationClient.Protect(Encoding.UTF8.GetBytes(key));
            Directory.CreateDirectory(AppConfig.UserDataFolder);
            string temp=KeyPath+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllBytes(temp,encrypted);File.Move(temp,KeyPath);}
            finally{if(File.Exists(temp))File.Delete(temp);}
            return key;
        }
    }
}
