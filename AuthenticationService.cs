using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Linq;

namespace CognexStyleApp
{
    public static class AuthenticationService
    {
        private static Dictionary<string, string> users = new Dictionary<string, string>();
        private static string usersFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PixTech", "users.dat");

        static AuthenticationService()
        {
            string dir = Path.GetDirectoryName(usersFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            LoadUsers();
        }

        public static bool ValidateUser(string username, string password)
        {
            string hashedPassword = HashPassword(password);
            if (users.ContainsKey(username)) return users[username] == hashedPassword;
            return false;
        }

        public static bool AddUser(string username, string password)
        {
            if (users.ContainsKey(username)) return false;
            users[username] = HashPassword(password);
            SaveUsers();
            return true;
        }

        public static bool DeleteUser(string username)
        {
            if (users.Remove(username)) { SaveUsers(); return true; }
            return false;
        }

        public static List<string> GetAllUsers() { return users.Keys.ToList(); }

        private static string HashPassword(string password)
        {
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
                StringBuilder builder = new StringBuilder();
                foreach (byte b in bytes) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        private static void SaveUsers()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (var user in users) lines.Add($"{user.Key}:{user.Value}");
                File.WriteAllLines(usersFilePath, lines);
            }
            catch { }
        }

        private static void LoadUsers()
        {
            try
            {
                if (File.Exists(usersFilePath))
                {
                    string[] lines = File.ReadAllLines(usersFilePath);
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split(':');
                        if (parts.Length == 2) users[parts[0]] = parts[1];
                    }
                }
            }
            catch { }
        }
    }
}