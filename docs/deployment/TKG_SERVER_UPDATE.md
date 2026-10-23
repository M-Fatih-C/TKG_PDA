# TKG PDA Server — Güncelleme Kılavuzu (Windows Server)

Bu belge, çalışan bir TKG PDA sunucusunun yeni bir sürüme veri kaybı olmadan güncellenmesini açıklar.

---

## 1. Güncelleme Prensipleri

TKG PDA güncelleme paketi (`TKG_PDA_Update_<version>.exe`) aşağıdaki koruma ilkelerine göre tasarlanmıştır:

1. **Veritabanı Korunur:** Mevcut kullanıcılar, şifreler, sicil kayıtları, makine/varyantlar, taşıma geçmişi, audit logları, RDS WorkSite kayıtları ve devreye alma durumları kesinlikle silinmez veya sıfırlanmaz.
2. **Otomatik Yedekleme:** Güncelleme öncesi veritabanı dump'ı (`pg_dump`), uygulama ikilileri ve yapılandırma `C:\ProgramData\TKG PDA\Backups\<zaman_damgası>\` altına kopyalanır.
3. **İleriye Dönük (Forward-Only) Migrasyon:** Yeni migrasyonlar güvenli ve idempotent şekilde uygulanır.
4. **Yapılandırma & Güvenlik:** `C:\ProgramData\TKG PDA\Config\appsettings.Production.json` ve DPAPI korumalı gizli anahtarlar korunur.
5. **Otomatik Rollback:** Yeni servis başlatılamaz veya sağlık kontrolleri (`/health/live`, `/health/ready`) geçemezse sistem otomatik olarak önceki sürüme geri döner.

---

## 2. Güncelleme Adımları

1. `TKG_PDA_Update_<version>.exe` dosyasını sunucuya kopyalayın.
2. Sağ tıklayıp **Yönetici olarak çalıştır** seçeneğini seçin.
3. Güncelleme sihirbazı mevcut kurulumu (`C:\Program Files\TKG PDA\`) otomatik tespit eder.
4. **Güncelle (Update)** butonuna tıklayın.

### Güncelleme Sırasında Otomatik Yürütülen İşlemler:
1. `TKG PDA Server` Windows Servisi durdurulur.
2. Mevcut veritabanı ve dosyaların yedeği alınır.
3. Yeni veritabanı migrasyonları varsa sırayla uygulanır.
4. `Server\` ve `Web\` (Admin & PDA) ikilileri yeni sürümle değiştirilir.
5. `TKG PDA Server` servisi başlatılır.
6. Canlılık ve veritabanı hazır olma doğrulaması yapılır.
7. İşlem sonucu başarıyla raporlanır.

---

## 3. Güncelleme Doğrulaması

Güncelleme sonrasında sürüm kontrolünü tarayıcınızdan yapabilirsiniz:

- URL: `http://<SUNUCU_IP>:5000/version`
- Örnek yanıt:
```json
{
  "version": "1.0.0",
  "commit": "10cf9de7",
  "service": "TKG PDA Server",
  "status": "ok"
}
```

Güncelleme loglarını incelemek için:
- `C:\ProgramData\TKG PDA\Logs\update_<zaman_damgası>.log`
