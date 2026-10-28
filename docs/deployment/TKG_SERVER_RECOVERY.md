# TKG PDA Server — Kurtarma ve Geri Alma Kılavuzu (Recovery & Rollback)

Bu belge, beklenmeyen bir donanım/yazılım arızası veya başarısız güncelleme durumunda TKG PDA Server sisteminin önceki kararlı durumuna geri döndürülmesini açıklar.

---

## 1. Otomatik Geri Alma (Automatic Rollback)

`TKG_PDA_Update_<version>.exe` veya `Update-TkgServer.ps1` çalışırken herhangi bir ikili dosya kopyalama hatası ya da servisin başlatılamaması durumunda:

- Güncelleyici otomatik olarak `C:\ProgramData\TKG PDA\Backups\<zaman_damgası>\` altındaki önceki çalışan `Server\` ve `Web\` dosyalarını geri yükler.
- Önceki sürüm servisini tekrar başlatır.
- Bu işlem kullanıcı müdahalesi olmadan otomatik gerçekleşir ve `C:\ProgramData\TKG PDA\Logs\update_<zaman_damgası>.log` dosyasına yazılır.

---

## 2. Manuel Geri Alma (Manual Rollback)

Eğer sisteme elle müdahale edilmesi gerekirse:

### Adım 1: Windows Servisini Durdurun
Yönetici PowerShell konsolunda:
```powershell
Stop-Service -Name "TKG PDA Server" -Force
```

### Adım 2: Yedekleri İnceleyin
`C:\ProgramData\TKG PDA\Backups\` klasöründe zaman damgalı yedekleri listeleyin:
```powershell
Get-ChildItem "C:\ProgramData\TKG PDA\Backups"
```

Her yedek klasörü şunları içerir:
- `Server\` — Uygulama ikili dosyaları
- `Web\` — Admin ve PDA arayüzleri
- `Config\` — appsettings.Production.json
- `database_<ad>.sql` — PostgreSQL veritabanı dump'ı
- `backup_manifest.json` — Yedekleme bilgileri

### Adım 3: İkili Dosyaları Geri Yükleyin
Hedeflenen yedek klasöründeki dosyaları kurulum dizinine geri kopyalayın:
```powershell
$backup = "C:\ProgramData\TKG PDA\Backups\2026-10-12_104959"
Copy-Item "$backup\Server\*" "C:\Program Files\TKG PDA\Server\" -Recurse -Force
Copy-Item "$backup\Web\*" "C:\Program Files\TKG PDA\Web\" -Recurse -Force
```

### Adım 4: Veritabanını Geri Yükleyin (Gerekirse)
Veritabanı şemasında geri alınması gereken bir değişiklik varsa:
```powershell
& "C:\Program Files\PostgreSQL\18\bin\psql.exe" -h 127.0.0.1 -p 5432 -U postgres -d tkg_pda -f "$backup\database_tkg_pda.sql"
```

### Adım 5: Servisi Başlatın ve Doğrulayın
```powershell
Start-Service -Name "TKG PDA Server"
pwsh "C:\Program Files\TKG PDA\Tools\test-installed-server.ps1" -ServerHost 127.0.0.1 -Port 5000
```

---

## 3. Windows Servis Kurtarma ve Hata Ayıklama

Servis başlatılamıyorsa:

1. **Uygulama Logları:**
   `C:\ProgramData\TKG PDA\Logs\` altındaki log dosyalarını inceleyin.
2. **Windows Olay Görüntüleyicisi (Event Viewer):**
   `Applications and Services Logs` -> `TKG PDA Server` veya `Application` loglarını kontrol edin.
3. **Manuel Konsol Testi:**
   Servis yerine uygulamayı doğrudan konsoldan çalıştırarak hata çıktısını görün:
   ```powershell
   & "C:\Program Files\TKG PDA\Server\TkgPda.Api.exe"
   ```
