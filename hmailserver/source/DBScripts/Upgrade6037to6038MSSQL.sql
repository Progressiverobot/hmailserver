ALTER TABLE hm_surblservers ADD surblresult nvarchar(255) NOT NULL DEFAULT ''

update hm_dbversion set value = 6038
