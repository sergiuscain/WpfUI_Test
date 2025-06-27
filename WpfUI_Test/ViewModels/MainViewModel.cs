using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WpfUI_Test.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        [ObservableProperty] private int totalProgress;
        [ObservableProperty] private int[] progress = new int[3];
        [ObservableProperty] private string errorText;

        [ObservableProperty]
        private string[] imageSources = new string[3]
        {
            "/Img/GreyBack.jpg",
            "/Img/GreyBack.jpg",
            "/Img/GreyBack.jpg"
        };

        [ObservableProperty]
        private string[] imageUrls = new string[3]
        {
            "Введите URL",
            "URL...",
            "URL..."
        };

        // Единый экземпляр HttpClient для всех загрузок
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Массив для отслеживания активных загрузок
        private readonly bool[] _isDownloading = new bool[3];

        // Токены отмены для каждой загрузки
        private readonly CancellationTokenSource[] _cancellationTokens = new CancellationTokenSource[3];

        // Команда для запуска загрузки изображения
        [RelayCommand]
        public async Task Start(string imageIndexText)
        {
            // Парсим индекс изображения из строки
            if (!int.TryParse(imageIndexText, out int imageIndex) || imageIndex < 0 || imageIndex >= 3)
            {
                ErrorText = "Некорректный индекс изображения";
                return;
            }

            // Проверяем валидность URL
            if (string.IsNullOrWhiteSpace(ImageUrls[imageIndex]) ||
                ImageUrls[imageIndex] == "Введите URL" ||
                ImageUrls[imageIndex] == "URL...")
            {
                ErrorText = $"Введите URL для изображения N{imageIndex + 1}";
                return;
            }

            if (!IsValidUrl(ImageUrls[imageIndex]))
            {
                ErrorText = $"Некорректный URL для изображения N{imageIndex + 1}";
                return;
            }

            // Проверяем, не выполняется ли уже загрузка
            if (_isDownloading[imageIndex])
            {
                ErrorText = $"Загрузка изображения N{imageIndex + 1} уже выполняется";
                return;
            }

            // Инициализируем токен отмены
            _cancellationTokens[imageIndex]?.Dispose();
            _cancellationTokens[imageIndex] = new CancellationTokenSource();

            try
            {
                _isDownloading[imageIndex] = true;
                ErrorText = $"Загрузка изображения N{imageIndex + 1}";
                Progress[imageIndex] = 0;
                UpdateTotalProgress();

                string url = ImageUrls[imageIndex];
                string fileName = GenerateSafeFileName(url, imageIndex);
                string savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), fileName);

                // Проверяем, существует ли файл, и удаляем его, если он заблокирован
                if (File.Exists(savePath))
                {
                    try
                    {
                        File.Delete(savePath);
                    }
                    catch (IOException ex)
                    {
                        ErrorText = $"Не удалось удалить существующий файл для изображения N{imageIndex + 1}: {ex.Message}";
                        Progress[imageIndex] = 0;
                        UpdateTotalProgress();
                        return;
                    }
                }

                // Создаем объект для отслеживания прогресса
                var progress = new Progress<int>(percent =>
                {
                    Progress[imageIndex] = percent;
                    UpdateTotalProgress();
                });

                // Выполняем загрузку файла
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cancellationTokens[imageIndex].Token);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                long totalReadBytes = 0;

                // Используем await using для гарантированного закры InMemoryRandom21
                await using var stream = await response.Content.ReadAsStreamAsync(_cancellationTokens[imageIndex].Token);
                await using var fileStream = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None);

                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await stream.ReadAsync(buffer, _cancellationTokens[imageIndex].Token)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cancellationTokens[imageIndex].Token);
                    totalReadBytes += bytesRead;
                    if (totalBytes > 0)
                    {
                        int progressPercentage = (int)((totalReadBytes * 100) / totalBytes);
                        ((IProgress<int>)progress).Report(progressPercentage);
                    }
                }

                // Явно закрываем FileStream перед обновлением ImageSources
                await fileStream.FlushAsync();
                fileStream.Close();

                // Проверяем, существует ли файл
                if (File.Exists(savePath))
                {
                    ErrorText = $"Изображение N{imageIndex + 1} успешно загружено";
                    ImageSources[imageIndex] = savePath;
                    OnPropertyChanged(nameof(ImageSources));
                }
                else
                {
                    ErrorText = $"Ошибка: файл изображения N{imageIndex + 1} не был создан";
                    Progress[imageIndex] = 0;
                    UpdateTotalProgress();
                }
            }
            catch (OperationCanceledException)
            {
                ErrorText = $"Загрузка изображения N{imageIndex + 1} отменена";
                Progress[imageIndex] = 0;
                UpdateTotalProgress();
            }
            catch (HttpRequestException ex)
            {
                ErrorText = $"Ошибка загрузки изображения N{imageIndex + 1}: {ex.Message}";
                Progress[imageIndex] = 0;
                UpdateTotalProgress();
            }
            catch (IOException ex) when (ex.Message.Contains("being used by another process"))
            {
                ErrorText = $"Ошибка: файл изображения N{imageIndex + 1} используется другим процессом: {ex.Message}";
                Progress[imageIndex] = 0;
                UpdateTotalProgress();
            }
            catch (Exception ex)
            {
                ErrorText = $"Ошибка: {ex.Message}";
                Progress[imageIndex] = 0;
                UpdateTotalProgress();
            }
            finally
            {
                _isDownloading[imageIndex] = false;
            }
        }

        // Команда для остановки загрузки
        [RelayCommand]
        public void Stop(string imageIndexText)
        {
            // Парсим индекс изображения
            if (!int.TryParse(imageIndexText, out int imageIndex) || imageIndex < 0 || imageIndex >= 3)
            {
                ErrorText = "Некорректный индекс изображения";
                return;
            }

            // Проверяем, выполняется ли загрузка
            if (_isDownloading[imageIndex] && _cancellationTokens[imageIndex] != null)
            {
                _cancellationTokens[imageIndex].Cancel();
                ErrorText = $"Загрузка изображения N{imageIndex + 1} отменена";
                Progress[imageIndex] = 0;
                UpdateTotalProgress();
            }
        }

        // Команда для загрузки всех изображений
        [RelayCommand]
        public async Task DownloadAll()
        {
            for (int i = 0; i < 3; i++)
            {
                if (!string.IsNullOrWhiteSpace(ImageUrls[i]) &&
                    ImageUrls[i] != "Введите URL" &&
                    ImageUrls[i] != "URL...")
                {
                    await Start(i.ToString());
                }
            }
        }

        // Проверяет, является ли URL валидным
        private bool IsValidUrl(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uriResult)
                   && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        }

        // Генерирует уникальное безопасное имя файла на основе URL и временной метки
        private string GenerateSafeFileName(string url, int imageIndex)
        {
            try
            {
                Uri uri = new Uri(url);
                string originalFileName = Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrEmpty(originalFileName))
                {
                    string safeName = string.Join("_", originalFileName.Split(Path.GetInvalidFileNameChars()));
                    // Добавляем временную метку для уникальности
                    return $"image_{imageIndex}_{DateTime.Now:yyyyMMddHHmmssfff}_{safeName}";
                }
            }
            catch
            {
                // В случае ошибки возвращаем имя файла с временной меткой
            }
            return $"image_{imageIndex}_{DateTime.Now:yyyyMMddHHmmssfff}.jpg";
        }

        // Обновляет общий прогресс загрузки
        private void UpdateTotalProgress()
        {
            int totalImages = Progress.Count(p => p > 0);
            if (totalImages == 0)
            {
                TotalProgress = 0;
                return;
            }
            TotalProgress = (int)Progress.Take(totalImages).Average();
        }

        // Освобождаем ресурсы при уничтожении объекта
        public void Dispose()
        {
            _httpClient.Dispose();
            foreach (var token in _cancellationTokens)
            {
                token?.Dispose();
            }
        }
    }
}