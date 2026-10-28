# TKG PDA Server — Kurulum Kılavuzu (Windows Server)

Bu belge, TKG PDA sisteminin temiz bir Windows Server üzerine **IIS gerektirmeden** tek bir Setup EXE ile kurulumunu açıklar.

---

## 1. Sistem Gereksinimleri

- **İşletim Sistemi:** Windows Server 2019 / 2022 / Windows 10/11 x64
- **Yetki:** Yönetici (Administrator) yetkileri
- **Veritabanı:** PostgreSQL 18 (veya 15-17). *Sunucuda yoksa kurulum sırasında otomatik hazırlanabilir.*
- **IIS:** **Gereksizdir.** Kestrel web sunucusu doğrudan yerel Windows Servisi olarak çalışır.
- **Node / npm / .NET SDK:** **Gereksizdir.** Sunucu paketi tamamen self-contained (bağımsız) derlenmiştir.

---

## 2. Kurulum Adımları

### Adım 1: Kurulum Dosyasını Çalıştırın
1. `TKG_PDA_Setup_1.0.0.exe` dosyasını sunucuya kopyalayın.
2. Sağ tıklayıp **Yönetici olarak çalıştır** seçeneğini seçin.

### Adım 2: Kurulum Sihirbazını Doldurun
Sihirbaz aşağıdaki adımları sırayla sunar:

1. **Kurulum Dizini:**
   - Varsayılan: `C:\Program Files\TKG PDA\` (Salt okunur uygulama ikilileri)
   - Değişken veriler, loglar ve yapılandırma otomatik olarak `C:\ProgramData\TKG PDA\` altına yerleştirilir.

2. **Sunucu Ayarları (Kestrel):**
   - **Port:** `5000` (veya tesis altyapınıza uygun boş bir port)
   - **IP / Bağlantı Adresi:** `0.0.0.0` (tüm ağ arayüzlerini dinler)

3. **Veritabanı Ayarları (PostgreSQL):**
   - **Host:** `127.0.0.1` (veya kurumsal veritabanı IP'si)
   - **Port:** `5432` (veya `55450`)
   - **Veritabanı Adı:** `tkg_pda`
   - **Kullanıcı:** `postgres`
   - **Parola:** Veritabanı parolanız

4. **RDS Robot Entegrasyon Ayarları:**
   - **RDS Base Adresi:** SEER RDS sunucu adresi (örn. `http://192.168.1.100:8080`)
   - **Callback URL:** TKG bildirim adresi (örn. `http://<SUNUCU_IP>:5000/api/integrations/rds/events`)
   - **Integration / Callback Key:** Boş bırakırsanız kurulum sihirbazı güvenli 32 baytlık kriptografik anahtarları otomatik üretir.

### Adım 3: Kurulumu Başlatın
- **Kur (Install)** butonuna basın.
- Kurulum otomatik olarak:
  - İkili dosyaları ve web arayüzlerini yükler,
  - Veritabanını oluşturur ve 26 adet SQL migrasyonunu + müşteri ana verilerini uygular,
  - `TKG PDA Server` adıyla Windows Servisini kaydeder ve otomatik başlatır,
  - Windows Güvenlik Duvarında seçilen port için kural açar,
  - Servisin canlılık (`/health/live`) ve veritabanı hazır olma (`/health/ready`) testlerini doğrular.

---

## 3. Kurulum Sonrası Erişim

Kurulum tamamlandığında tarayıcınızdan aşağıdaki adreslere erişebilirsiniz:

| Arayüz | URL | Açıklama |
|---|---|---|
| **Ana Karşılama Sayfası** | `http://<SUNUCU_IP>:5000/` | PDA ve Yönetim Paneli hızlı erişim linkleri ve durum |
| **Yönetim Paneli (Admin Web)** | `http://<SUNUCU_IP>:5000/admin/` | Sistem yönetimi, makine/varyant ayarları, RDS paneli |
| **PDA / Operatör Terminali** | `http://<SUNUCU_IP>:5000/pda/` | Hat operatörleri, yarı mamül / mamül ambar ekranları |
| **Sağlık Kontrolü** | `http://<SUNUCU_IP>:5000/health/live` | Sistem liveness kontrolü |
| **Hazır Olma Kontrolü** | `http://<SUNUCU_IP>:5000/health/ready` | Veritabanı bağlantı kontrolü |
| **Sürüm Bilgisi** | `http://<SUNUCU_IP>:5000/version` | Sürüm, commit hash ve durum JSON çıktısı |

---

## 4. RDS Entegrasyon Betiğinin Yüklenmesi

SEER RDS tarafına yapıştırılacak entegrasyon betiği kurulum sırasında oluşturulmuştur:

- **Dosya Konumu:** `C:\ProgramData\TKG PDA\RDS\tkg_integration_install.js`

1. Bu dosyanın içeriğini açıp kopyalayın.
2. SEER RDS web arayüzünde **Script / Entegrasyon** bölümüne yapıştırıp kaydedin.
