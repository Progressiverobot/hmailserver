ALTER TABLE hm_fetchaccounts ADD famirrorfolders tinyint not null CONSTRAINT df_famirrorfolders DEFAULT 0

update hm_dbversion set value = 6031
