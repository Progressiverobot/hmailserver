ALTER TABLE hm_contacts ADD contacturi nvarchar(255) NOT NULL DEFAULT ''

ALTER TABLE hm_contacts ADD contactuid nvarchar(255) NOT NULL DEFAULT ''

ALTER TABLE hm_contacts ADD contactvcard ntext NOT NULL DEFAULT ''

update hm_dbversion set value = 6040
