ALTER TABLE hm_fetchaccounts ADD famirrorfolders tinyint not null DEFAULT 0

update hm_dbversion set value = 6031
