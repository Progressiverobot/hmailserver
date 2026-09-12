ALTER TABLE hm_surblservers ADD COLUMN surblresult varchar(255) NOT NULL DEFAULT '';

update hm_dbversion set value = 6038;
