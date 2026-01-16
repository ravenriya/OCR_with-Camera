using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Linq;

namespace PixtechApplication
{
    public static class AuthenticationService
    {
        private static Dictionary<string, string> users = new Dictionary<string, string>();
        private static Dictionary<string, int> failedAttempts = new Dictionary<string, int>();
        private static Dictionary<string, DateTime> lockoutTime = new Dictionary<string, DateTime>();

        private static string usersFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PixTech", "users.dat");

        private const int MAX_FAILED_ATTEMPTS = 3;
        private const int LOCKOUT_MINUTES = 5;

        static AuthenticationService()
        {
            string dir = Path.GetDirectoryName(usersFilePath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            LoadUsers();
        }

        public static bool IsAccountLocked(string username)
        {
            if (lockoutTime.ContainsKey(username))
            {
                TimeSpan remaining = lockoutTime[username] - DateTime.Now;
                if (remaining.TotalSeconds > 0) 
                {
                    return true;
                }
                else
                {
                    lockoutTime.Remove(username);
                    failedAttempts[username] = 0;
                    return false;
                }
            }
            return false;
        }

        public static TimeSpan GetLockoutTimeRemaining(string username)
        {
            if (lockoutTime.ContainsKey(username))
            {
                TimeSpan remaining = lockoutTime[username] - DateTime.Now;
                return remaining.TotalSeconds > 0 ? remaining : TimeSpan.Zero;
            }
            return TimeSpan.Zero;
        }

        public static int GetFailedAttempts(string username)
        {
            return failedAttempts.ContainsKey(username) ? failedAttempts[username] : 0;
        }

        public static int GetRemainingAttempts(string username)
        {
            int failed = GetFailedAttempts(username);
            return Math.Max(0, MAX_FAILED_ATTEMPTS - failed);
        }

        public static bool ValidateUser(string username, string password)
        {
            if (IsAccountLocked(username)) return false;
            if (!users.ContainsKey(username)) return false;

            string hashedPassword = HashPassword(password);
            bool isValid = users[username] == hashedPassword;

            if (isValid)
            {
                if (failedAttempts.ContainsKey(username))
                    failedAttempts[username] = 0;
            }
            else
            {
                if (!failedAttempts.ContainsKey(username))
                    failedAttempts[username] = 0;

                failedAttempts[username]++;

                if (failedAttempts[username] >= MAX_FAILED_ATTEMPTS)
                {
                    lockoutTime[username] = DateTime.Now.AddMinutes(LOCKOUT_MINUTES);
                }
            }

            return isValid;
        }

        public static bool AddUser(string username, string password)
        {
            if (users.ContainsKey(username)) return false;
            users[username] = HashPassword(password);
            failedAttempts[username] = 0;
            SaveUsers();
            return true;
        }

        public static bool DeleteUser(string username)
        {
            if (users.Remove(username))
            {
                failedAttempts.Remove(username);
                lockoutTime.Remove(username);
                SaveUsers();
                return true;
            }
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
                        if (parts.Length == 2)
                        {
                            users[parts[0]] = parts[1];
                            failedAttempts[parts[0]] = 0;
                        }
                    }
                }
            }
            catch { }
        }
    }
}